using System.Globalization;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;
using IConfigurationManager = MediaBrowser.Common.Configuration.IConfigurationManager;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class GuestEncodingTests
{
    private readonly Mock<IMediaEncoder> _encoder = new();
    private readonly GuestEncoding _encoding;
    private readonly User _owner = new("host", "auth", "reset");
    private readonly MediaSourceInfo _source;
    public GuestEncodingTests()
    {
        var paths = Mock.Of<IApplicationPaths>();
        var config = new Mock<IConfigurationManager>(); config.SetupGet(x => x.CommonApplicationPaths).Returns(paths);
        _encoder.SetupGet(x => x.EncoderVersion).Returns(new Version(8, 1));
        _encoder.Setup(x => x.SupportsEncoder(It.IsAny<string>())).Returns(true);
        _encoder.Setup(x => x.CanEncodeToAudioCodec(It.IsAny<string>())).Returns(true);
        _encoder.Setup(x => x.GetInputPathArgument(It.IsAny<EncodingJobInfo>())).Returns("\"/test/party.mp4\"");
        _encoder.Setup(x => x.GetTimeParameter(It.IsAny<long>())).Returns((long t) => (t / (double)TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture));
        _encoding = new GuestEncoding(new EncodingHelper(paths, _encoder.Object, Mock.Of<ISubtitleEncoder>(),
            new ConfigurationBuilder().Build(), config.Object, Mock.Of<IPathManager>()), config.Object, Mock.Of<IMediaSourceManager>());
        foreach (var kind in new[] { PermissionKind.EnablePlaybackRemuxing, PermissionKind.EnableVideoPlaybackTranscoding, PermissionKind.EnableAudioPlaybackTranscoding })
            _owner.Permissions.Add(new Permission(kind, true));
        _source = new MediaSourceInfo
        {
            Id = "test", Path = "/test/party.mp4", Protocol = MediaProtocol.File, VideoType = VideoType.VideoFile,
            Container = "mp4", RunTimeTicks = 180 * TimeSpan.TicksPerSecond,
            MediaStreams = [new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Profile = "High",
                BitDepth = 8, BitRate = 1000000, Width = 640, Height = 360, RealFrameRate = 24, AverageFrameRate = 24,
                PixelFormat = "yuv420p", IsAVC = true, NalLengthSize = "4", ColorTransfer = "bt709" },
                new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Profile = "LC", Channels = 2,
                    BitRate = 96000, SampleRate = 48000 }]
        };
    }
    private GuestEncodingPlan Build(EncodingOptions? options = null, int bitrate = 2000)
        => _encoding.Build(_source, _owner, 0, bitrate, options ?? new EncodingOptions(), _source.Path);
    private void Deny(PermissionKind kind) => _owner.Permissions.Single(p => p.Kind == kind).Value = false;

    [Fact] public void CompatibleSourceRemuxesWithoutAnyEncoderOrScaling()
    {
        var plan = Build();
        Assert.Equal("Remux", plan.PlaybackMethod);
        Assert.Equal("copy", plan.VideoEncoder);
        Assert.Contains("-codec:v:0 copy", plan.Arguments);
        Assert.Contains("-codec:a:0 copy", plan.Arguments);
        Assert.DoesNotContain("libx264", plan.Arguments);
        Assert.DoesNotContain("-vf", plan.Arguments);
    }
    [Fact] public void CompatibleVideoOnlyTranscodesIncompatibleAudio()
    {
        _source.MediaStreams[1].Codec = "dts";
        var plan = Build();
        Assert.Equal("DirectStream", plan.PlaybackMethod);
        Assert.Equal("copy", plan.VideoEncoder);
        Assert.False(plan.CopyAudio);
    }
    [Theory]
    [InlineData(HardwareAccelerationType.nvenc, "h264_nvenc")]
    [InlineData(HardwareAccelerationType.qsv, "h264_qsv")]
    [InlineData(HardwareAccelerationType.vaapi, "h264_vaapi")]
    [InlineData(HardwareAccelerationType.amf, "h264_amf")]
    [InlineData(HardwareAccelerationType.videotoolbox, "h264_videotoolbox")]
    [InlineData(HardwareAccelerationType.rkmpp, "h264_rkmpp")]
    public void RequiredConversionUsesJellyfinsConfiguredHardwareEncoder(HardwareAccelerationType type, string expected)
    {
        _source.MediaStreams[0].Codec = "mpeg4";
        var plan = Build(new EncodingOptions { HardwareAccelerationType = type, EnableHardwareEncoding = true });
        Assert.Equal("Transcode", plan.PlaybackMethod);
        Assert.Equal(expected, plan.VideoEncoder);
        Assert.Contains("-codec:v:0 " + expected, plan.Arguments);
        Assert.DoesNotContain("libx264", plan.Arguments);
    }
    [Fact] public void HardwarePreferenceDoesNotForceTranscodingOfCompatibleVideo()
    {
        Assert.Equal("copy", Build(new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.nvenc, EnableHardwareEncoding = true }).VideoEncoder);
    }
    [Fact] public void HighBitDepthVideoIsNotBlindlyCopiedToTheBrowserProfile()
    {
        _source.MediaStreams[0].BitDepth = 10;
        _source.MediaStreams[0].Profile = "High 10";
        Assert.False(Build().CopyVideo);
    }
    [Fact] public void HdrSourceIsNotPassedThroughAsSdr()
    {
        _source.MediaStreams[0].ColorTransfer = "smpte2084";
        Assert.False(Build().CopyVideo);
    }
    [Fact] public void ExplicitlyDisabledHardwareEncodingUsesNativeSoftwareChoiceWhenRequired()
    {
        _source.MediaStreams[0].Codec = "mpeg4";
        Assert.Equal("libx264", Build(new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.nvenc, EnableHardwareEncoding = false }).VideoEncoder);
    }
    [Fact] public void UnavailableConfiguredEncoderUsesJellyfinsFallbackChoice()
    {
        _source.MediaStreams[0].Codec = "mpeg4";
        _encoder.Setup(x => x.SupportsEncoder("h264_nvenc")).Returns(false);
        Assert.Equal("libx264", Build(new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.nvenc, EnableHardwareEncoding = true }).VideoEncoder);
    }
    [Fact] public void ExceedingBitrateLimitRequiresConversionAndHonorsServerThreadCountAndPreset()
    {
        var plan = Build(new EncodingOptions { EncodingThreadCount = 1, EncoderPreset = EncoderPreset.slow }, 500);
        Assert.False(plan.CopyVideo);
        Assert.Contains("-threads 1", plan.Arguments);
        Assert.Contains("-preset slow", plan.Arguments);
        Assert.DoesNotContain("scale=1280:-2", plan.Arguments);
    }
    [Fact] public void RemuxDoesNotRequireVideoOrAudioTranscodePermission()
    {
        Deny(PermissionKind.EnableVideoPlaybackTranscoding); Deny(PermissionKind.EnableAudioPlaybackTranscoding);
        Assert.Equal("Remux", Build().PlaybackMethod);
    }
    [Fact] public void RequiredVideoConversionIsDeniedInsteadOfForcingIncompatibleCopy()
    {
        Deny(PermissionKind.EnableVideoPlaybackTranscoding);
        _source.MediaStreams[0].Codec = "mpeg4";
        Assert.Equal(403, Assert.Throws<PartyException>(() => Build()).Status);
    }
    [Fact] public void RequiredAudioConversionAndRemuxPermissionsAreEnforced()
    {
        Deny(PermissionKind.EnableAudioPlaybackTranscoding);
        _source.MediaStreams[1].Codec = "dts";
        Assert.Throws<PartyException>(() => Build());
        _source.MediaStreams[1].Codec = "aac";
        Deny(PermissionKind.EnablePlaybackRemuxing);
        Assert.Throws<PartyException>(() => Build());
    }
    [Fact] public void EncodingSettingsChangesInvalidateStreamPlan()
    {
        var options = new EncodingOptions(); var before = GuestEncoding.SettingsKey(options);
        options.EnableHardwareEncoding = !options.EnableHardwareEncoding;
        Assert.NotEqual(before, GuestEncoding.SettingsKey(options));
    }
    [Fact] public void NonLocalOrReplacedSourcesAreRejected()
    {
        _source.Protocol = MediaProtocol.Http;
        Assert.Throws<PartyException>(() => Build());
        _source.Protocol = MediaProtocol.File; _source.EncoderPath = "https://evil.example/media";
        Assert.Throws<PartyException>(() => Build());
    }
    [Theory]
    [InlineData("ass")]
    [InlineData("pgssub")]
    public void PartySelectedStyledOrBitmapSubtitlesUseNativeBurnInAndRequireVideoConversion(string codec)
    {
        _source.MediaStreams = [.._source.MediaStreams, new MediaStream { Index = 2, Type = MediaStreamType.Subtitle, Codec = codec }];
        _encoder.Setup(x => x.EscapeSubtitleFilterPath(It.IsAny<string>())).Returns("/test/subtitles.ass");
        var plan = _encoding.Build(_source, _owner, 0, 2000, new EncodingOptions(), _source.Path, 2);
        Assert.False(plan.CopyVideo);
        Assert.Equal(2, plan.BurnedSubtitleIndex);
        Assert.Contains(codec == "ass" ? "subtitles=" : "overlay", plan.Arguments);
        Deny(PermissionKind.EnableVideoPlaybackTranscoding);
        Assert.Equal(403, Assert.Throws<PartyException>(() => _encoding.Build(_source, _owner, 0, 2000, new EncodingOptions(), _source.Path, 2)).Status);
    }
}
