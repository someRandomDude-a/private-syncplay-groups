using System.Diagnostics;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using Jellyfin.Database.Implementations.Enums;

namespace Jellyfin.Plugin.PrivateSyncPlay;

// One shared HLS process per party, never one encoder per anonymous viewer.
// There is no generic proxy, client-selected item ID, file path or FFmpeg argument.
public sealed class GuestMedia(PartyStore store, ISessionManager sessions, ILibraryManager library,
    IMediaEncoder encoder, IApplicationPaths paths, IUserManager users, GuestEncoding encoding) : IDisposable
{
    private readonly Dictionary<Guid, StreamJob> _jobs = [];
    private readonly string _root = Path.Combine(paths.CachePath, "private-syncplay", Guid.NewGuid().ToString("N"));
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".ts", ".m2ts", ".mpg", ".mpeg" };

    public object State(Party party)
    {
        var owner = CheckOwner(party);
        // The creator need not remain, but at least one authenticated party member MUST.
        var member = PlaybackMember(party);
        if (member?.NowPlayingItem is null)
        {
            Stop(party.Id);
            return new { Playing = false, Paused = true };
        }
        var video = library.GetItemById<Video>(member.NowPlayingItem.Id, party.Owner);
        if (video is null || !VideoExtensions.Contains(Path.GetExtension(video.Path)) || !File.Exists(video.Path))
        {
            Stop(party.Id);
            throw new PartyException(415, "Guest viewing supports local video files only.");
        }
        var position = Math.Max(0, (member.PlayState.PositionTicks ?? 0) / (double)TimeSpan.TicksPerSecond);
        var paused = member.PlayState.IsPaused;
        _jobs.TryGetValue(party.Id, out var job);
        if (paused)
        {
            Stop(party.Id);
            return new { Playing = true, Paused = true, Title = video.Name, Position = position };
        }
        var bitrate = Plugin.Instance!.Configuration.GuestVideoBitrateKbps;
        var options = encoding.Options;
        var settingsKey = GuestEncoding.SettingsKey(options);
        if (job is not null && job.Process.HasExited && store.Now - job.Started < TimeSpan.FromSeconds(10))
            throw new PartyException(503, "Guest stream failed. Check Jellyfin's transcoding and hardware acceleration settings.");
        if (job is null || job.Item != video.Id || job.Process.HasExited || job.Bitrate != bitrate
            || job.Plan.SettingsKey != settingsKey
            || Math.Abs(position - (job.Offset + (store.Now - job.Started).TotalSeconds)) > 4)
        {
            Stop(party.Id);
            job = Start(party.Id, video, position, bitrate, encoding.Build(video, owner, position, bitrate, options));
            _jobs[party.Id] = job;
        }
        return new { Playing = true, Paused = false, Title = video.Name, Position = position,
            job.Generation, job.Offset, job.Plan.PlaybackMethod, Ready = File.Exists(Path.Combine(job.Directory, "index.m3u8")) };
    }

    private StreamJob Start(Guid party, Video video, double offset, int bitrate, GuestEncodingPlan plan)
    {
        var generation = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(_root, party.ToString("N"), generation);
        Directory.CreateDirectory(directory);
        var info = new ProcessStartInfo(encoder.EncoderPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            WorkingDirectory = directory, Arguments = plan.Arguments
        };
        var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new InvalidOperationException();
            // Drain without recording potentially sensitive paths in FFmpeg diagnostics.
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            return new StreamJob(video.Id, generation, directory, offset, store.Now, bitrate, process, plan);
        }
        catch
        {
            process.Dispose();
            Directory.Delete(directory, true);
            throw new PartyException(503, "Guest encoder could not start. Check the server FFmpeg configuration.");
        }
    }

    public (byte[] Bytes, string Type) Read(Party party, string generation, string name)
    {
        CheckOwner(party);
        if (!_jobs.TryGetValue(party.Id, out var job) || job.Generation != generation
            || (name != "index.m3u8" && !System.Text.RegularExpressions.Regex.IsMatch(name, @"\Asegment[0-9]{9}\.ts\z")))
            throw new PartyException(404, "Stream unavailable.");
        // Revalidate media permissions and the authoritative currently playing item on EVERY segment.
        var current = PlaybackMember(party);
        if (current?.NowPlayingItem?.Id != job.Item || current.PlayState.IsPaused || library.GetItemById<Video>(job.Item, party.Owner) is null)
        { Stop(party.Id); throw new PartyException(410, "Stream ended."); }
        try
        {
            var file = Path.Combine(job.Directory, name);
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > 16 * 1024 * 1024) throw new PartyException(503, "Stream segment too large.");
            using var output = new MemoryStream();
            input.CopyTo(output);
            return (output.ToArray(), name == "index.m3u8" ? "application/vnd.apple.mpegurl" : "video/mp2t");
        }
        catch (IOException) { throw new PartyException(404, "Stream is preparing."); }
    }

    public void Reap()
    {
        foreach (var id in _jobs.Keys.ToArray())
            if (!store.Parties.ContainsKey(id) || store.GuestCount(id) == 0) Stop(id);
    }
    private SessionInfo? PlaybackMember(Party party) => sessions.Sessions.Where(s => party.Members.Contains(s.Id))
        .OrderByDescending(s => s.Id == party.HostSession).FirstOrDefault(s => s.NowPlayingItem is not null);
    private Jellyfin.Database.Implementations.Entities.User CheckOwner(Party party)
    {
        var owner = users.GetUserById(party.Owner);
        bool Allowed(PermissionKind kind) => owner?.Permissions.Any(p => p.Kind == kind && p.Value) == true;
        if (owner is null || Allowed(PermissionKind.IsDisabled) || !Allowed(PermissionKind.EnableMediaPlayback)
            || owner.SyncPlayAccess == SyncPlayUserAccessType.None)
        { Stop(party.Id); throw new PartyException(403, "Guest playback is no longer permitted."); }
        if (_jobs.TryGetValue(party.Id, out var job))
        {
            try { GuestEncoding.EnsurePermissions(owner, job.Plan.CopyVideo, job.Plan.CopyAudio); }
            catch (PartyException) { Stop(party.Id); throw; }
        }
        return owner;
    }
    private void Stop(Guid party)
    {
        if (!_jobs.Remove(party, out var job)) return;
        try { if (!job.Process.HasExited) { job.Process.Kill(true); job.Process.WaitForExit(3000); } }
        finally
        {
            job.Process.Dispose();
            try { Directory.Delete(job.Directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    public void Dispose() { lock (store.Gate) { foreach (var id in _jobs.Keys.ToArray()) Stop(id); } }
    private sealed record StreamJob(Guid Item, string Generation, string Directory, double Offset, DateTimeOffset Started, int Bitrate, Process Process, GuestEncodingPlan Plan);
}
