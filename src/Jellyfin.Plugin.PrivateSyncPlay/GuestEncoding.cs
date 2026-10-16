using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.PrivateSyncPlay;

// Jellyfin owns codec/HWA selection, device initialization, filters, presets and thread policy.
// The plugin owns only its conservative shared browser profile and restricted HLS transport.
public sealed class GuestEncoding(EncodingHelper helper, IConfigurationManager configuration, IMediaSourceManager sources)
{
    public EncodingOptions Options => configuration.GetEncodingOptions();
    public static string SettingsKey(EncodingOptions options)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(options))));

    public GuestEncodingPlan Build(Video video, User owner, double offset, int bitrateKbps, EncodingOptions options)
    {
        var path = Path.GetFullPath(video.Path);
        var source = sources.GetStaticMediaSources(video, false, owner).FirstOrDefault(s =>
            s.Protocol == MediaProtocol.File && s.VideoType == VideoType.VideoFile && s.Path == video.Path
            && (string.IsNullOrEmpty(s.EncoderPath) || s.EncoderPath == s.Path)
            && (s.EncoderProtocol is null or MediaProtocol.File));
        if (source is null) throw new PartyException(415, "A local video media source is required.");
        return Build(source, owner, offset, bitrateKbps, options, path);
    }

    // The validated source overload also lets tests exercise Jellyfin's real encoding helpers.
    public GuestEncodingPlan Build(MediaBrowser.Model.Dto.MediaSourceInfo source, User owner, double offset,
        int bitrateKbps, EncodingOptions options, string expectedPath)
    {
        if (source.Protocol != MediaProtocol.File || source.VideoType != VideoType.VideoFile
            || source.Path != expectedPath || (!string.IsNullOrEmpty(source.EncoderPath) && source.EncoderPath != source.Path)
            || source.EncoderProtocol is not (null or MediaProtocol.File))
            throw new PartyException(415, "Only the authorized local media source may be streamed.");
        var request = new BaseEncodingJobOptions
        {
            Container = "ts", VideoCodec = "h264", AudioCodec = "aac", VideoBitRate = bitrateKbps * 1000,
            AudioBitRate = 128000, MaxAudioChannels = 2, MaxVideoBitDepth = 8,
            Profile = "high", VideoRangeType = "SDR", SubtitleStreamIndex = -1,
            StartTimeTicks = (long)(offset * TimeSpan.TicksPerSecond), EnableAudioVbrEncoding = true
        };
        var state = new EncodingJobInfo(TranscodingJobType.Hls)
        {
            BaseRequest = request, IsVideoRequest = true, IsInputVideo = true, User = owner,
            VideoType = VideoType.VideoFile, OutputContainer = "ts", OutputVideoCodec = "h264", OutputAudioCodec = "aac",
            SupportedVideoCodecs = ["h264"], SupportedAudioCodecs = ["aac"]
        };
        helper.AttachMediaSourceInfo(state, options, source, "index.ts");
        if (state.VideoStream is null) throw new PartyException(415, "Video stream metadata is unavailable.");
        state.OutputVideoBitrate = helper.GetVideoBitrateParamValue(request, state.VideoStream, "h264");
        state.OutputAudioChannels = helper.GetNumAudioChannelsParam(state, state.AudioStream, "aac");
        state.OutputAudioBitrate = helper.GetAudioBitrateParam(request, state.AudioStream, state.OutputAudioChannels);
        var copyVideo = helper.CanStreamCopyVideo(state, state.VideoStream);
        var copyAudio = state.AudioStream is null || (helper.CanStreamCopyAudio(state, state.AudioStream, state.SupportedAudioCodecs)
            && (copyVideo || options.HlsAudioSeekStrategy != HlsAudioSeekStrategy.TranscodeAudio));
        if (copyVideo) state.OutputVideoCodec = "copy";
        if (copyAudio) state.OutputAudioCodec = "copy";
        EnsurePermissions(owner, copyVideo, copyAudio);
        if (!copyVideo)
        {
            // Match Jellyfin's bitrate-driven sizing instead of always scaling/upscaling to 1280px.
            if (state.VideoStream.BitRate.HasValue && request.VideoBitRate >= state.VideoStream.BitRate)
            {
                request.MaxWidth = state.VideoStream.Width;
                request.MaxHeight = state.VideoStream.Height;
            }
            else
            {
                var resolution = ResolutionNormalizer.Normalize(state.VideoStream.BitRate, state.OutputVideoBitrate!.Value,
                    state.OutputVideoBitrate.Value, null, null, state.TargetFramerate);
                request.MaxWidth = resolution.MaxWidth;
                request.MaxHeight = resolution.MaxHeight;
            }
        }

        // All platform-specific encoder/decoder names and GPU/filter flags come from Jellyfin.
        var videoEncoder = helper.GetVideoEncoder(state, options);
        var videoArgs = copyVideo ? helper.GetProgressiveVideoArguments(state, options, videoEncoder, EncoderPreset.veryfast)
            : $"-codec:v:0 {videoEncoder} {helper.GetVideoProcessingFilterParam(state, options, videoEncoder)} "
                + helper.GetVideoQualityParam(state, videoEncoder, options, EncoderPreset.veryfast)
                + helper.GetHlsVideoKeyFrameArguments(state, videoEncoder, 2, false, null);
        var audioArgs = helper.GetProgressiveVideoAudioArguments(state, options);
        var args = $"-nostdin -hide_banner -loglevel error -protocol_whitelist file,pipe "
            + $"{helper.GetInputModifier(state, options, "ts")} -readrate 1 -readrate_initial_burst 12 "
            + $"{helper.GetInputArgument(state, options, "ts")} {helper.GetMapArgs(state)} "
            + $"-sn -dn -map_metadata -1 -map_chapters -1 "
            + $"-threads {EncodingHelper.GetNumberOfThreads(state, options, videoEncoder)} {videoArgs} {audioArgs} "
            + helper.GetAudioBitStreamArguments(state, "ts", source.Container)
            + " -f hls -hls_time 2 -hls_list_size 10 -hls_flags delete_segments+independent_segments+temp_file"
            + " -hls_segment_filename segment%09d.ts -y index.m3u8";
        return new GuestEncodingPlan(args, videoEncoder, copyVideo, copyAudio, SettingsKey(options));
    }

    public static void EnsurePermissions(User owner, bool copyVideo, bool copyAudio)
    {
        bool Allowed(PermissionKind kind) => owner.Permissions.Any(p => p.Kind == kind && p.Value);
        if ((copyVideo && !Allowed(PermissionKind.EnablePlaybackRemuxing))
            || (!copyVideo && !Allowed(PermissionKind.EnableVideoPlaybackTranscoding))
            || (!copyAudio && !Allowed(PermissionKind.EnableAudioPlaybackTranscoding)))
            throw new PartyException(403, "The required media conversion is not permitted for this party's owner.");
    }
}

public sealed record GuestEncodingPlan(string Arguments, string VideoEncoder, bool CopyVideo, bool CopyAudio, string SettingsKey)
{
    public string PlaybackMethod => CopyVideo ? CopyAudio ? "Remux" : "DirectStream" : "Transcode";
}
