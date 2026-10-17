using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.PrivateSyncPlay;

// Read-only borrowing of known native MPEG-TS HLS output. No core-job pings, stops, mutations,
// token forwarding, arbitrary URLs, directory scanning, or deletion of native cache files.
public sealed class GuestNativeStreams : IDisposable
{
    private readonly PartyStore _store;
    private readonly ISessionManager _sessions;
    private readonly ITranscodeManager _transcodes;
    private readonly IConfigurationManager _configuration;
    private readonly IApplicationPaths _paths;
    private readonly Dictionary<string, Playback> _playbacks = new(StringComparer.OrdinalIgnoreCase);

    public GuestNativeStreams(PartyStore store, ISessionManager sessions, ITranscodeManager transcodes, IConfigurationManager configuration, IApplicationPaths paths)
    {
        _store = store; _sessions = sessions; _transcodes = transcodes; _configuration = configuration; _paths = paths;
        sessions.PlaybackStart += Remember;
        sessions.PlaybackProgress += Remember;
        sessions.PlaybackStopped += Forget;
        sessions.SessionEnded += SessionEnded;
    }

    private void Remember(object? sender, PlaybackProgressEventArgs e)
    {
        lock (_store.Gate)
        {
            foreach (var key in _playbacks.Keys.Where(key => !_store.Parties.Values.Any(p => p.Members.ContainsKey(key))).ToArray()) _playbacks.Remove(key);
            if (e.Session is null || e.Item is null || e.PlaySessionId is not { Length: > 0 and <= 128 }
                || _store.ForSession(e.Session.Id, e.Session.UserId) is null || _playbacks.Count >= 10000) return;
            _playbacks[e.Session.Id] = new(e.Session.UserId, e.Item.Id, e.PlaySessionId);
        }
    }
    private void Forget(object? sender, PlaybackStopEventArgs e)
    {
        lock (_store.Gate)
            if (e.Session is not null && _playbacks.TryGetValue(e.Session.Id, out var playback) && playback.PlaySession == e.PlaySessionId) _playbacks.Remove(e.Session.Id);
    }
    private void SessionEnded(object? sender, SessionEventArgs e) { lock (_store.Gate) _playbacks.Remove(e.SessionInfo.Id); }

    public NativeGuestStream? Capture(Party party, IEnumerable<SessionInfo> candidates, Video video, int videoBitrateKbps, double position, int? burnedSubtitle, int? audioIndex = null)
    {
        if (burnedSubtitle.HasValue) return null; // Native burn-in cannot be proven equivalent from the public job metadata.
        foreach (var session in candidates.OrderByDescending(s => s.TranscodingInfo?.Bitrate ?? 0))
        {
            try
            {
                if (!party.IsMember(session.Id, session.UserId) || session.NowPlayingItem?.Id != video.Id) continue;
                if (!_playbacks.TryGetValue(session.Id, out var playback) || playback.User != session.UserId || playback.Item != video.Id) continue;
                var job = _transcodes.GetTranscodingJob(playback.PlaySession);
                if (job?.PlaySessionId != playback.PlaySession || !Compatible(job, session, video.Path, videoBitrateKbps, audioIndex) || job!.Path is null) continue;
                var root = _configuration.GetEncodingOptions().TranscodingTempPath;
                if (string.IsNullOrEmpty(root)) root = Path.Combine(_paths.CachePath, "transcodes");
                if (!SafeFile(root, job.Path) || !Regex.IsMatch(Path.GetFileName(job.Path), @"\A[a-fA-F0-9]{32}\.m3u8\z")) continue;
                var window = Window(job.Path, position);
                if (window is null) continue;
                var info = session.TranscodingInfo!;
                return new(session.Id, session.UserId, playback.PlaySession, video.Id, video.Path, root, job.Path, job, videoBitrateKbps,
                    info.IsVideoDirect, info.AudioCodec is null || info.IsAudioDirect, audioIndex, window);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { /* Unsupported/changing native output falls back to the isolated guest encoder. */ }
        }
        return null;
    }

    public bool IsCurrent(Party party, IEnumerable<SessionInfo> candidates, NativeGuestStream stream, int cap)
    {
        var session = candidates.FirstOrDefault(s => s.Id == stream.Session && s.UserId == stream.User && party.IsMember(s.Id, s.UserId));
        return session is not null && _playbacks.TryGetValue(session.Id, out var playback) && playback.PlaySession == stream.PlaySession
            && playback.Item == stream.Item && ReferenceEquals(_transcodes.GetTranscodingJob(stream.PlaySession), stream.Job)
            && stream.Job.Path == stream.Playlist && Compatible(stream.Job, session, stream.SourcePath, Math.Min(cap, stream.VideoBitrateKbps), stream.AudioIndex);
    }

    private static bool Compatible(TranscodingJob? job, SessionInfo session, string sourcePath, int bitrate, int? audioIndex)
    {
        var info = session.TranscodingInfo;
        if (job is null || job.Type != TranscodingJobType.Hls || job.DeviceId != session.DeviceId || job.MediaSource?.Path != sourcePath
            || job.CancellationTokenSource?.IsCancellationRequested == true || session.PlayState.IsPaused
            || session.PlayState.SubtitleStreamIndex is >= 0 || info is null || info.Container != "ts" || info.VideoCodec != "h264"
            || info.Bitrate is not > 0
            || (session.PlayState.AudioStreamIndex ?? job.MediaSource.DefaultAudioStreamIndex ?? SingleAudioIndex(job.MediaSource)) != audioIndex
            || (info.AudioCodec is not (null or "aac")) || info.AudioChannels is > 2) return false;
        var sourceVideo = job.MediaSource.MediaStreams.FirstOrDefault(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video);
        if (info.IsVideoDirect)
        {
            // Native TranscodingInfo may report the REQUESTED budget even when stream-copying.
            // In that case use proven source rates, not a fictitious re-encoding bitrate.
            if (sourceVideo?.BitRate is not > 0 || sourceVideo.BitRate > bitrate * 1000) return false;
            var audio = job.MediaSource.MediaStreams.FirstOrDefault(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio && s.Index == audioIndex);
            if (info.AudioCodec is not null && (!info.IsAudioDirect || audio?.BitRate is not (> 0 and <= 128000))) return false;
        }
        else if (info.Bitrate > bitrate * 1000) return false; // Conservative combined-rate ceiling when output rate separation is unavailable.
        // Do not mistake high-10/HDR copied video for the SDR/8-bit guest profile.
        return sourceVideo is { BitDepth: 8, PixelFormat: "yuv420p" or "yuvj420p" or "nv12" }
            && sourceVideo.VideoRange == Jellyfin.Data.Enums.VideoRange.SDR && (!info.IsVideoDirect || sourceVideo.Codec == "h264");
    }
    private static int? SingleAudioIndex(MediaBrowser.Model.Dto.MediaSourceInfo source)
    {
        var audio = source.MediaStreams.Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio).Take(2).ToArray();
        return audio.Length == 1 ? audio[0].Index : null;
    }

    private static NativeGuestWindow? Window(string playlist, double position)
    {
        using var input = new FileStream(playlist, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length is <= 0 or > 256 * 1024) return null;
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        var lines = Encoding.UTF8.GetString(bytes).Split('\n');
        if (lines[0].Trim() != "#EXTM3U") return null;
        var directory = Path.GetDirectoryName(playlist)!;
        var prefix = Path.GetFileNameWithoutExtension(playlist);
        var entries = new List<NativeGuestSegment>();
        double duration = 0, offset = 0;
        foreach (var raw in lines.Skip(1))
        {
            var line = raw.Trim(); if (line.Length == 0) continue;
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal)) { if (line != "#EXT-X-MEDIA-SEQUENCE:0") return null; }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                if (duration != 0 || !double.TryParse(line[8..].Split(',')[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out duration)
                    || !double.IsFinite(duration) || duration is <= 0 or > 30) return null;
            }
            else if (line.StartsWith('#'))
            {
                if (!line.StartsWith("#EXT-X-VERSION:", StringComparison.Ordinal) && !line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal)
                    && line is not ("#EXT-X-ENDLIST" or "#EXT-X-PLAYLIST-TYPE:VOD" or "#EXT-X-PLAYLIST-TYPE:EVENT" or "#EXT-X-INDEPENDENT-SEGMENTS")) return null;
            }
            else
            {
                if (duration <= 0 || !Regex.IsMatch(line, "\\A" + Regex.Escape(prefix) + "[0-9]{1,9}\\.ts\\z")) return null;
                if (!int.TryParse(line[prefix.Length..^3], out var index) || index != entries.Count) return null; // No ambiguous seek-offset mapping.
                entries.Add(new(entries.Count, line, offset, duration)); offset += duration; duration = 0;
                if (entries.Count > 10000) return null;
            }
        }
        if (duration != 0 || !entries.Any(e => e.Offset <= position && e.Offset + e.Duration > position)) return null;
        var selected = entries.Where(e => e.Offset + e.Duration > Math.Max(0, position - 12) && e.Offset < position + 12).Take(16).ToArray();
        if (selected.Length == 0 || selected.Any(e => !SafeFile(directory, Path.Combine(directory, e.File)))) return null;
        return new(directory, selected);
    }

    public static (byte[] Bytes, string Type) Read(NativeGuestStream stream, string name)
    {
        if (name == "index.m3u8")
        {
            var entries = stream.Window.Segments;
            var text = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:")
                .Append((int)Math.Ceiling(entries.Max(e => e.Duration))).Append("\n#EXT-X-MEDIA-SEQUENCE:").Append(entries[0].Index).Append('\n');
            foreach (var segment in entries)
                text.Append("#EXTINF:").Append(segment.Duration.ToString("0.######", CultureInfo.InvariantCulture)).Append(",\nsegment").Append(segment.Index.ToString("D9", CultureInfo.InvariantCulture)).Append(".ts\n");
            return (Encoding.UTF8.GetBytes(text.ToString()), "application/vnd.apple.mpegurl");
        }
        var entry = stream.Window.Segments.FirstOrDefault(e => name == "segment" + e.Index.ToString("D9", CultureInfo.InvariantCulture) + ".ts")
            ?? throw new PartyException(404, "Stream segment unavailable.");
        var file = Path.Combine(stream.Window.Directory, entry.File);
        if (!SafeFile(stream.CacheRoot, file)) throw new PartyException(404, "Stream segment unavailable.");
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > 16 * 1024 * 1024) throw new PartyException(503, "Stream segment too large.");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        return (bytes, "video/mp2t");
    }

    private static bool SafeFile(string root, string file)
    {
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(file) || !File.Exists(file)) return false;
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); file = Path.GetFullPath(file);
        var relative = Path.GetRelativePath(root, file);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var directory = Path.GetDirectoryName(file); !string.Equals(directory, root, comparison); directory = Path.GetDirectoryName(directory))
            if (directory is null || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    public void Dispose()
    {
        _sessions.PlaybackStart -= Remember; _sessions.PlaybackProgress -= Remember; _sessions.PlaybackStopped -= Forget; _sessions.SessionEnded -= SessionEnded;
        lock (_store.Gate) _playbacks.Clear();
    }
    private sealed record Playback(Guid User, Guid Item, string PlaySession);
}

public sealed record NativeGuestSegment(int Index, string File, double Offset, double Duration);
public sealed record NativeGuestWindow(string Directory, NativeGuestSegment[] Segments);
public sealed record NativeGuestStream(string Session, Guid User, string PlaySession, Guid Item, string SourcePath, string CacheRoot, string Playlist,
    TranscodingJob Job, int VideoBitrateKbps, bool CopyVideo, bool CopyAudio, int? AudioIndex, NativeGuestWindow Window);
