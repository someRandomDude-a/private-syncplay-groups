using System.Diagnostics;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using Jellyfin.Database.Implementations.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.PrivateSyncPlay;

// One shared HLS process per party, never one encoder per anonymous viewer.
// There is no generic proxy, client-selected item ID, file path or FFmpeg argument.
public sealed class GuestMedia : IDisposable
{
    private readonly PartyStore store;
    private readonly ISessionManager sessions;
    private readonly ILibraryManager library;
    private readonly IMediaEncoder encoder;
    private readonly IUserManager users;
    private readonly GuestEncoding encoding;
    private readonly Func<Configuration>? configuration;
    private readonly PluginStatus? status;
    private readonly Dictionary<Guid, StreamJob> _jobs = [];
    private readonly Dictionary<Guid, DateTimeOffset> _retryAfter = [];
    private readonly Dictionary<Guid, GuestQuality> _quality = [];
    private readonly Dictionary<Guid, BorrowedStream> _borrowed = [];
    private readonly GuestNativeStreams? _native;
    private readonly ILogger<GuestMedia> _logger;
    private bool _disposed;
    private Configuration Config => configuration?.Invoke() ?? Plugin.Instance?.Configuration
        ?? throw new PartyException(503, "Guest playback is unavailable.");
    private readonly string _root;
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".ts", ".m2ts", ".mpg", ".mpeg" };

    public GuestMedia(PartyStore store, ISessionManager sessions, ILibraryManager library,
        IMediaEncoder encoder, IApplicationPaths paths, IUserManager users, GuestEncoding encoding,
        Func<Configuration>? configuration = null, PluginStatus? status = null, ILogger<GuestMedia>? logger = null, GuestNativeStreams? native = null)
    {
        this.store = store;
        this.sessions = sessions;
        this.library = library;
        this.encoder = encoder;
        this.users = users;
        this.encoding = encoding;
        this.configuration = configuration;
        this.status = status;
        _native = native;
        _logger = logger ?? NullLogger<GuestMedia>.Instance;
        _root = Path.Combine(paths.CachePath, "private-syncplay", Guid.NewGuid().ToString("N"));
        store.GuestAccessEnded += GuestAccessEnded;
    }

    public GuestPlaybackState State(Party party)
    {
        EnsureAvailable(party);
        var owner = CheckOwner(party);
        // The creator need not remain, but at least one authenticated party member MUST.
        var member = PlaybackMember(party);
        if (member?.NowPlayingItem is null)
        {
            Stop(party.Id);
            return new() { Playing = false, Paused = true };
        }
        var video = library.GetItemById<Video>(member.NowPlayingItem.Id, owner);
        if (video is null || !VideoExtensions.Contains(Path.GetExtension(video.Path)) || !File.Exists(video.Path))
        {
            Stop(party.Id);
            throw new PartyException(415, "Guest viewing supports local video files only.");
        }
        var position = Math.Max(0, (member.PlayState.PositionTicks ?? 0) / (double)TimeSpan.TicksPerSecond);
        var source = encoding.Source(video, owner);
        var available = source.MediaStreams.Where(s => GuestSubtitlePolicy.Allowed(source, s)).ToArray();
        var selectedIndex = member.PlayState.SubtitleStreamIndex ?? source.DefaultSubtitleStreamIndex ?? -1;
        var audioIndex = member.PlayState.AudioStreamIndex ?? source.DefaultAudioStreamIndex;
        var selected = available.FirstOrDefault(s => s.Index == selectedIndex);
        var burnIndex = selected is not null && GuestSubtitlePolicy.NeedsBurnIn(selected) ? selected.Index : (int?)null;
        var tracks = available.Where(s => Config.GuestPlayer.Allows("subtitles") && !GuestSubtitlePolicy.NeedsBurnIn(s))
            .Select(s => new GuestSubtitleTrack(s.Index, (s.Language ?? "Unknown language") + (s.IsForced ? " · forced" : ""), s.Language)).ToArray();
        var duration = (video.RunTimeTicks ?? 0) / (double)TimeSpan.TicksPerSecond;
        var paused = member.PlayState.IsPaused;
        _jobs.TryGetValue(party.Id, out var job);
        if (paused)
        {
            Stop(party.Id);
            return new() { Playing = true, Paused = true, Title = Config.GuestPlayer.Allows("title") ? video.Name : null,
                Position = position, Duration = duration, Offset = job?.Offset ?? position };
        }
        var quality = Quality(party.Id);
        var bitrate = quality.Select(Config.GuestVideoBitrateKbps, store.Guests(party.Id).Select(g => g.Id).ToArray(),
            _borrowed.GetValueOrDefault(party.Id)?.Generation ?? _jobs.GetValueOrDefault(party.Id)?.Generation, store.Now);
        var options = encoding.Options;
        var settingsKey = GuestEncoding.SettingsKey(options);
        var shared = _native?.Capture(party, NativeCandidates(party, video.Id), video, bitrate, position, burnIndex, audioIndex);
        if (shared is not null)
        {
            GuestEncoding.EnsurePermissions(owner, shared.CopyVideo, shared.CopyAudio);
            if (!_jobs.ContainsKey(party.Id) && !_borrowed.ContainsKey(party.Id) && _jobs.Count + _borrowed.Count >= Config.MaxGuestStreams)
                throw new PartyException(503, "The server's guest stream limit has been reached.");
            if (!_borrowed.TryGetValue(party.Id, out var borrowed) || !ReferenceEquals(borrowed.Stream.Job, shared.Job)
                || borrowed.Stream.Session != shared.Session || Math.Abs(position - borrowed.Position - (store.Now - borrowed.Started).TotalSeconds) > 4)
            {
                Stop(party.Id); EnsureAvailable(party);
                borrowed = new(shared, Guid.NewGuid().ToString("N"), shared.Window.Segments[0].Offset, position, store.Now);
            }
            else borrowed = borrowed with { Stream = shared };
            _borrowed[party.Id] = borrowed;
            return new() { Playing = true, Paused = false, Title = Config.GuestPlayer.Allows("title") ? video.Name : null,
                Position = position, Duration = duration, Subtitles = tracks, SelectedSubtitleIndex = Config.GuestPlayer.Allows("subtitles") ? selectedIndex : null,
                Generation = borrowed.Generation, Offset = borrowed.Offset, PlaybackMethod = "Shared", VideoBitrateKbps = bitrate, Ready = true };
        }
        _borrowed.Remove(party.Id); // Losing a native stream never gives guests authority to sustain/restart it.
        if (job is not null && job.Process.HasExited && store.Now - job.Started < TimeSpan.FromSeconds(10))
            throw new PartyException(503, "Guest stream failed. Check Jellyfin's transcoding and hardware acceleration settings.");
        if (job is null || job.Item != video.Id || job.Process.HasExited || job.Bitrate != bitrate
            || job.Plan.BurnedSubtitleIndex != burnIndex
            || job.Plan.SelectedAudioIndex != audioIndex
            || job.Plan.SettingsKey != settingsKey
            || Math.Abs(position - (job.Offset + (store.Now - job.Started).TotalSeconds)) > 4)
        {
            if (_retryAfter.TryGetValue(party.Id, out var retry) && retry > store.Now)
                throw new PartyException(503, "Guest stream is temporarily unavailable.");
            Stop(party.Id);
            EnsureAvailable(party);
            if (_jobs.Count + _borrowed.Count >= Config.MaxGuestStreams)
                throw new PartyException(503, "The server's guest stream limit has been reached.");
            var plan = encoding.Build(video, owner, position, bitrate, options, selectedIndex, audioIndex);
            _retryAfter[party.Id] = store.Now.AddSeconds(5);
            job = Start(party.Id, video, position, bitrate, plan);
            _jobs[party.Id] = job;
            _retryAfter.Remove(party.Id);
        }
        return new() { Playing = true, Paused = false, Title = Config.GuestPlayer.Allows("title") ? video.Name : null, Position = position, Duration = duration,
            Subtitles = tracks, SelectedSubtitleIndex = Config.GuestPlayer.Allows("subtitles") ? selectedIndex : null, BurnedSubtitleIndex = burnIndex,
            Generation = job.Generation, Offset = job.Offset, PlaybackMethod = job.Plan.PlaybackMethod, VideoBitrateKbps = bitrate,
            Ready = File.Exists(Path.Combine(job.Directory, "index.m3u8")) };
    }

    private GuestQuality Quality(Guid party) => _quality.TryGetValue(party, out var quality) ? quality : _quality[party] = new();
    public void ReportQuality(Party party, Guid guest, string generation, int? bandwidthKbps, double bufferSeconds, bool stalled)
    {
        EnsureAvailable(party);
        var current = _borrowed.GetValueOrDefault(party.Id)?.Generation ?? _jobs.GetValueOrDefault(party.Id)?.Generation;
        if (current != generation) return; // Ignore stale generations, never restart for feedback.
        if (!store.Guests(party.Id).Any(g => g.Id == guest)) throw new PartyException(401, "Guest session ended.");
        Quality(party.Id).Report(guest, generation, bandwidthKbps, bufferSeconds, stalled, store.Now);
    }

    private StreamJob Start(Guid party, Video video, double offset, int bitrate, GuestEncodingPlan plan)
    {
        var generation = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(_root, party.ToString("N"), generation);
        var info = new ProcessStartInfo(encoder.EncoderPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            WorkingDirectory = directory, Arguments = plan.Arguments
        };
        var process = new Process { StartInfo = info };
        var started = false;
        try
        {
            Directory.CreateDirectory(directory);
            started = process.Start();
            if (!started) throw new InvalidOperationException();
            // Drain without recording potentially sensitive paths in FFmpeg diagnostics.
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            return new StreamJob(video.Id, generation, directory, offset, store.Now, bitrate, process, plan);
        }
        catch
        {
            if (started) Terminate(process); else process.Dispose();
            DeleteDirectory(directory);
            throw new PartyException(503, "Guest encoder could not start. Check the server FFmpeg configuration.");
        }
    }

    public (byte[] Bytes, string Type) Read(Party party, string generation, string name)
    {
        EnsureAvailable(party);
        var owner = CheckOwner(party);
        if (_borrowed.TryGetValue(party.Id, out var borrowed) && borrowed.Generation == generation)
        {
            RequireBorrowed(party, borrowed, owner);
            try { return GuestNativeStreams.Read(borrowed.Stream, name); }
            catch (IOException) { throw new PartyException(404, "Stream is preparing."); }
        }
        if (!_jobs.TryGetValue(party.Id, out var job) || job.Generation != generation
            || name is null || name.Length > 32
            || (name != "index.m3u8" && !System.Text.RegularExpressions.Regex.IsMatch(name, @"\Asegment[0-9]{9}\.ts\z")))
            throw new PartyException(404, "Stream unavailable.");
        // Revalidate media permissions and the authoritative currently playing item on EVERY segment.
        var current = PlaybackMember(party);
        if (job.Bitrate > Config.GuestVideoBitrateKbps || current?.NowPlayingItem?.Id != job.Item || current.PlayState.IsPaused || library.GetItemById<Video>(job.Item, owner) is null)
        { Stop(party.Id); throw new PartyException(410, "Stream ended."); }
        try
        {
            var file = Path.Combine(job.Directory, name);
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = input.Length;
            if (length > (name == "index.m3u8" ? 256 * 1024 : 16 * 1024 * 1024)) throw new PartyException(503, "Stream segment too large.");
            var bytes = new byte[(int)length];
            input.ReadExactly(bytes); // Fixed allocation even if a file grows concurrently.
            return (bytes, name == "index.m3u8" ? "application/vnd.apple.mpegurl" : "video/mp2t");
        }
        catch (IOException) { throw new PartyException(404, "Stream is preparing."); }
    }

    // Authorization snapshot for asynchronous native subtitle conversion. Caller must revalidate before delivery.
    public GuestSubtitleRequest Subtitle(Party party, string generation, int index)
    {
        EnsureAvailable(party);
        GuestPlayerPolicy.Require(Config.GuestPlayer, "subtitles");
        var owner = CheckOwner(party);
        var borrowed = _borrowed.GetValueOrDefault(party.Id);
        if (borrowed is not null && borrowed.Generation == generation) RequireBorrowed(party, borrowed, owner);
        var job = _jobs.GetValueOrDefault(party.Id);
        var item = borrowed?.Generation == generation ? borrowed.Stream.Item : job?.Generation == generation ? job.Item : (Guid?)null;
        var offset = borrowed?.Generation == generation ? borrowed.Offset : job?.Offset ?? 0;
        if (!item.HasValue) throw new PartyException(404, "Stream unavailable.");
        var current = PlaybackMember(party);
        var video = library.GetItemById<Video>(item.Value, owner);
        if (current?.NowPlayingItem?.Id != item || current.PlayState.IsPaused || video is null) throw new PartyException(410, "Stream ended.");
        var source = encoding.Source(video, owner);
        var track = source.MediaStreams.FirstOrDefault(s => s.Index == index && GuestSubtitlePolicy.Allowed(source, s));
        if (track is null || GuestSubtitlePolicy.NeedsBurnIn(track)) throw new PartyException(404, "Subtitle track unavailable.");
        return new GuestSubtitleRequest(video, source.Id, index, offset);
    }

    public void Reap()
    {
        foreach (var id in _jobs.Keys.ToArray())
            if (!store.Parties.ContainsKey(id) || store.GuestCount(id) == 0) Stop(id);
        foreach (var id in _borrowed.Keys.ToArray())
            if (!store.Parties.ContainsKey(id) || store.GuestCount(id) == 0) Stop(id);
        if (!_disposed)
            foreach (var id in _borrowed.Keys.Concat(_jobs.Keys).Skip(Config.MaxGuestStreams).ToArray()) Stop(id);
        foreach (var id in _retryAfter.Keys.Where(id => !store.Parties.ContainsKey(id) || store.GuestCount(id) == 0).ToArray()) _retryAfter.Remove(id);
    }
    private void EnsureAvailable(Party party)
    {
        status?.EnsureAvailable();
        if (_disposed) throw new PartyException(503, "Guest playback is unavailable.");
        if (!store.Parties.TryGetValue(party.Id, out var current) || !ReferenceEquals(party, current))
            throw new PartyException(410, "Party ended.");
        if (store.GuestCount(party.Id) == 0)
        { Stop(party.Id); throw new PartyException(410, "Guest access ended."); }
        // Do not wait for the maintenance timer to invalidate a disconnected/disabled final member.
        var active = sessions.Sessions.Where(s => party.IsMember(s.Id, s.UserId) && Eligible(users.GetUserById(s.UserId))).ToArray();
        if (active.Length == 0) { store.Close(party.Id); Stop(party.Id); throw new PartyException(410, "Party ended."); }
    }
    private static bool Eligible(Jellyfin.Database.Implementations.Entities.User? user)
        => user is not null && user.SyncPlayAccess != SyncPlayUserAccessType.None
            && !user.Permissions.Any(p => p.Kind == PermissionKind.IsDisabled && p.Value)
            && user.Permissions.Any(p => p.Kind == PermissionKind.EnableMediaPlayback && p.Value);
    private SessionInfo? PlaybackMember(Party party) => sessions.Sessions.Where(s => party.IsMember(s.Id, s.UserId)
            && party.CanControl(s.UserId) && Eligible(users.GetUserById(s.UserId)))
        .OrderByDescending(s => s.Id == party.HostSession).FirstOrDefault(s => s.NowPlayingItem is not null);
    private IEnumerable<SessionInfo> NativeCandidates(Party party, Guid item) => sessions.Sessions.Where(s => party.IsMember(s.Id, s.UserId)
        && s.NowPlayingItem?.Id == item && !s.PlayState.IsPaused && Eligible(users.GetUserById(s.UserId)));
    private void RequireBorrowed(Party party, BorrowedStream borrowed, Jellyfin.Database.Implementations.Entities.User owner)
    {
        var current = PlaybackMember(party);
        if (current?.NowPlayingItem?.Id != borrowed.Stream.Item || current.PlayState.IsPaused
            || library.GetItemById<Video>(borrowed.Stream.Item, owner) is null
            || _native?.IsCurrent(party, NativeCandidates(party, borrowed.Stream.Item), borrowed.Stream, Config.GuestVideoBitrateKbps) != true)
        { Stop(party.Id); throw new PartyException(410, "Stream ended."); }
    }
    private Jellyfin.Database.Implementations.Entities.User CheckOwner(Party party)
    {
        var owner = users.GetUserById(party.Owner);
        bool Allowed(PermissionKind kind) => owner?.Permissions.Any(p => p.Kind == kind && p.Value) == true;
        if (!Eligible(owner) || !Allowed(PermissionKind.EnableMediaPlayback) || !Config.CanInviteGuests(party.Owner))
        { store.Close(party.Id); Stop(party.Id); throw new PartyException(403, "Guest playback is no longer permitted."); }
        if (_jobs.TryGetValue(party.Id, out var job))
        {
            try { GuestEncoding.EnsurePermissions(owner!, job.Plan.CopyVideo, job.Plan.CopyAudio); }
            catch (PartyException) { Stop(party.Id); throw; }
        }
        if (_borrowed.TryGetValue(party.Id, out var borrowed))
            GuestEncoding.EnsurePermissions(owner!, borrowed.Stream.CopyVideo, borrowed.Stream.CopyAudio);
        return owner!;
    }
    private void Stop(Guid party)
    {
        _borrowed.Remove(party); // Borrowed streams are owned exclusively by Jellyfin/the signed-in player.
        if (!_jobs.Remove(party, out var job)) return;
        Terminate(job.Process);
        DeleteDirectory(job.Directory);
    }
    private void GuestAccessEnded(Guid party)
    {
        _quality.Remove(party);
        _retryAfter.Remove(party);
        Stop(party);
    }
    private void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited) { process.Kill(true); if (!process.WaitForExit(1000)) DisableAfterCleanupFailure(); }
        }
        catch (Exception error)
        {
            DisableAfterCleanupFailure();
            _logger.LogWarning("Guest encoder cleanup failed ({ErrorType}).", error.GetType().Name);
        }
        finally { process.Dispose(); }
    }
    private void DisableAfterCleanupFailure()
    {
        _disposed = true;
        status?.Disable("Guest encoder cleanup failed. Guest streaming is disabled until restart; check for an orphaned encoder process.");
        foreach (var id in store.Parties.Keys.ToArray()) store.Close(id);
        _logger.LogError("Guest streaming disabled because encoder termination could not be confirmed.");
    }
    private void DeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (Exception error) { _logger.LogWarning("Guest cache cleanup failed ({ErrorType}).", error.GetType().Name); }
    }
    public void Dispose()
    {
        lock (store.Gate)
        {
            store.GuestAccessEnded -= GuestAccessEnded;
            _disposed = true;
            foreach (var id in _jobs.Keys.ToArray()) Stop(id);
            _retryAfter.Clear();
            _quality.Clear();
            _borrowed.Clear();
            DeleteDirectory(_root);
        }
    }
    private sealed record StreamJob(Guid Item, string Generation, string Directory, double Offset, DateTimeOffset Started, int Bitrate, Process Process, GuestEncodingPlan Plan);
    private sealed record BorrowedStream(NativeGuestStream Stream, string Generation, double Offset, double Position, DateTimeOffset Started);
}

public sealed record GuestSubtitleTrack(int Index, string Label, string? Language);
public sealed record GuestSubtitleRequest(Video Video, string SourceId, int Index, double Offset);
public sealed record GuestPlaybackState
{
    public bool Playing { get; init; }
    public bool Paused { get; init; }
    public string? Title { get; init; }
    public double Position { get; init; }
    public double Duration { get; init; }
    public double Offset { get; init; }
    public string? Generation { get; init; }
    public bool Ready { get; init; }
    public string? PlaybackMethod { get; init; }
    public int VideoBitrateKbps { get; init; }
    public GuestSubtitleTrack[] Subtitles { get; init; } = [];
    public int? SelectedSubtitleIndex { get; init; }
    public int? BurnedSubtitleIndex { get; init; }
    public GuestPlayerView? Player { get; init; }
}
