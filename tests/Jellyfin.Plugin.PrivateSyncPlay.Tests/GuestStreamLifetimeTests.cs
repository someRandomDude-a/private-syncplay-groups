using System.Collections;
using System.Diagnostics;
using System.Reflection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class GuestStreamLifetimeTests : IDisposable
{
    private readonly ManualClock _clock = new();
    private readonly PartyStore _store;
    private readonly Configuration _config = new() { EnableGuestInvites = true, DefaultGuestPermission = true };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "opencode", "guest-stream-lifetime-" + Guid.NewGuid().ToString("N"));
    private readonly GuestMedia _media;
    private readonly List<Process> _processes = [];

    public GuestStreamLifetimeTests()
    {
        _store = new PartyStore(_clock);
        var paths = new Mock<IApplicationPaths>(); paths.SetupGet(p => p.CachePath).Returns(_root);
        _media = new GuestMedia(_store, Mock.Of<ISessionManager>(), Mock.Of<ILibraryManager>(), Mock.Of<IMediaEncoder>(), paths.Object,
            Mock.Of<IUserManager>(), new GuestEncoding(null!, Mock.Of<IConfigurationManager>(), Mock.Of<IMediaSourceManager>()), () => _config);
    }

    private (Party Party, string Guest, Invite Invite) Admit()
    {
        var party = _store.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString(), GroupMode.Private, 4);
        var invite = _store.Issue(party, party.Owner, true, 3, null, _config);
        return (party, _store.RedeemGuest(invite.Token, null, _config), invite.Value);
    }

    private (int Pid, string Directory) StartJob(Party party)
    {
        // Substitute a harmless long-lived OS process for FFmpeg, but exercise the real production
        // Stop/Terminate/cache-cleanup path. Real encoder shutdown is covered by Docker integration.
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
        if (OperatingSystem.IsWindows()) { start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add("ping -n 120 127.0.0.1 >nul"); }
        else { start.ArgumentList.Add("-c"); start.ArgumentList.Add("sleep 120"); }
        var process = Process.Start(start)!; _processes.Add(process);
        var directory = Path.Combine(_root, party.Id.ToString("N")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "segment000000000.ts"), "test");
        var jobType = typeof(GuestMedia).GetNestedType("StreamJob", BindingFlags.NonPublic)!;
        var job = Activator.CreateInstance(jobType, [Guid.NewGuid(), "generation", directory, 0d, _clock.GetUtcNow(), 1000,
            process, new GuestEncodingPlan("", "test", true, true, "test")]);
        var jobs = (IDictionary)typeof(GuestMedia).GetField("_jobs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_media)!;
        jobs.Add(party.Id, job);
        return (process.Id, directory);
    }

    private static bool Running(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Theory]
    [InlineData("leave")]
    [InlineData("revoke")]
    [InlineData("expiry")]
    [InlineData("guest-disable")]
    [InlineData("party-guest-disable")]
    [InlineData("party-close")]
    [InlineData("last-account")]
    public void FinalGuestRevocationStopsTheProcessAndDeletesOnlyItsCacheBeforeReturning(string reason)
    {
        var first = Admit(); var job = StartJob(first.Party);
        var other = Admit(); var otherJob = StartJob(other.Party);
        switch (reason)
        {
            case "leave": _store.LeaveGuest(first.Guest); break;
            case "revoke": _store.Revoke(first.Party, first.Invite.Id); break;
            case "expiry":
                _clock.Advance(20); _store.GuestSession(other.Guest, _config, heartbeat: true);
                _clock.Advance(11); _store.Sweep(_config); break;
            case "guest-disable":
                _config.Users.Add(new UserSettings { UserId = first.Party.Owner, GuestInvites = PermissionOverride.Deny }); _store.Sweep(_config); break;
            case "party-guest-disable":
                first.Party.Policy = first.Party.Policy with { AllowGuests = false }; _store.Sweep(_config); break;
            case "party-close": _store.Close(first.Party.Id); break;
            case "last-account": _store.RemoveSession(first.Party.HostSession); break;
        }
        Assert.False(Running(job.Pid)); Assert.False(Directory.Exists(job.Directory));
        Assert.True(Running(otherJob.Pid)); Assert.True(Directory.Exists(otherJob.Directory));
        Assert.Throws<PartyException>(() => _media.State(first.Party)); // Cannot restart a zero-guest job.
    }

    [Fact] public void OneGuestLeavingDoesNotStopAStreamStillUsedByAnotherGuest()
    {
        var first = Admit();
        var invite = _store.Issue(first.Party, first.Party.Owner, true, 2, null, _config);
        var remaining = _store.RedeemGuest(invite.Token, null, _config);
        var job = StartJob(first.Party);
        _store.LeaveGuest(first.Guest);
        Assert.True(Running(job.Pid)); Assert.True(Directory.Exists(job.Directory));
        _store.LeaveGuest(remaining);
        Assert.False(Running(job.Pid)); Assert.False(Directory.Exists(job.Directory));
    }

    [Fact] public void BrowserSwitchingPartiesStopsTheAbandonedPartysStream()
    {
        var first = Admit(); var job = StartJob(first.Party);
        var other = Admit();
        var invite = _store.Issue(other.Party, other.Party.Owner, true, 2, null, _config);
        _store.RedeemGuest(invite.Token, first.Guest, _config);
        Assert.False(Running(job.Pid)); Assert.False(Directory.Exists(job.Directory));
    }

    [Fact] public void BrowserReplacingItsAdmissionWithinTheSamePartyDoesNotInterruptTheStream()
    {
        var first = Admit(); var job = StartJob(first.Party);
        var invite = _store.Issue(first.Party, first.Party.Owner, true, 2, null, _config);
        _store.RedeemGuest(invite.Token, first.Guest, _config);
        Assert.Equal(1, _store.GuestCount(first.Party.Id));
        Assert.True(Running(job.Pid)); Assert.True(Directory.Exists(job.Directory));
    }

    public void Dispose()
    {
        _media.Dispose();
        foreach (var process in _processes)
        {
            try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(1000); } }
            catch (InvalidOperationException) { /* Production cleanup already disposed it. */ }
            finally { process.Dispose(); }
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
}
