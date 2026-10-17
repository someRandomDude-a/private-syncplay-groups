using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class GuestLifetimeTests : IDisposable
{
    private readonly PartyStore _store = new(TimeProvider.System);
    private readonly Configuration _config = new() { EnableGuestInvites = true, DefaultGuestPermission = true };
    private readonly User _owner = new("owner", "auth", "reset");
    private readonly User _friend = new("friend", "auth", "reset");
    private readonly List<SessionInfo> _sessions = [];
    private readonly GuestMedia _media;
    private readonly Party _party;
    private readonly string _guest;

    public GuestLifetimeTests()
    {
        var sessions = new Mock<ISessionManager>(); sessions.SetupGet(s => s.Sessions).Returns(_sessions);
        _sessions.Add(new SessionInfo(sessions.Object, NullLogger.Instance) { Id = "host", UserId = _owner.Id });
        var users = new Mock<IUserManager>(); users.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns((Guid id) => id == _owner.Id ? _owner : id == _friend.Id ? _friend : null);
        users.Setup(u => u.GetUserById(Guid.Empty)).Throws(new ArgumentException("No user identity"));
        var paths = new Mock<IApplicationPaths>(); paths.SetupGet(p => p.CachePath).Returns(Path.GetTempPath());
        _media = new GuestMedia(_store, sessions.Object, Mock.Of<ILibraryManager>(), Mock.Of<IMediaEncoder>(), paths.Object, users.Object,
            new GuestEncoding(null!, Mock.Of<IConfigurationManager>(), Mock.Of<IMediaSourceManager>()), () => _config);
        _party = _store.Create(Guid.NewGuid(), _owner.Id, "host", GroupMode.Private, 3);
        _guest = _store.RedeemGuest(_store.Issue(_party, _owner.Id, true, 2, null, _config).Token, null, _config);
    }

    [Fact] public void DisconnectedFinalMemberIsRejectedBeforeMaintenanceRuns()
    {
        _sessions.Clear();
        Assert.Equal(410, Assert.Throws<PartyException>(() => _media.State(_party)).Status);
        Assert.Equal(401, Assert.Throws<PartyException>(() => _store.GuestParty(_guest, _config)).Status);
    }
    [Fact] public void LoggedOutSessionCannotTriggerAnInvalidUserLookupOrKeepGuestsAlive()
    {
        _sessions[0].UserId = Guid.Empty;
        Assert.Equal(410, Assert.Throws<PartyException>(() => _media.State(_party)).Status);
        Assert.Empty(_store.Parties);
    }
    [Fact] public void ReassignedFinalMemberCannotKeepGuestsAliveBeforeMaintenanceRuns()
    {
        _friend.SyncPlayAccess = SyncPlayUserAccessType.CreateAndJoinGroups;
        _friend.Permissions.Add(new Permission(PermissionKind.EnableMediaPlayback, true));
        _sessions[0].UserId = _friend.Id;
        Assert.Equal(410, Assert.Throws<PartyException>(() => _media.State(_party)).Status);
        Assert.Empty(_store.Parties);
        Assert.Throws<PartyException>(() => _store.GuestParty(_guest, _config));
    }
    [Fact] public void RemainingDisabledMemberCannotKeepOwnerSharingAlive()
    {
        var sessions = Mock.Of<ISessionManager>();
        _sessions.Clear(); _sessions.Add(new SessionInfo(sessions, NullLogger.Instance) { Id = "friend", UserId = _friend.Id });
        _party.Members.Add("friend", _friend.Id);
        _friend.Permissions.Add(new Permission(PermissionKind.IsDisabled, true));
        Assert.Equal(410, Assert.Throws<PartyException>(() => _media.Read(_party, "known-generation", "index.m3u8")).Status);
        Assert.Empty(_store.Parties);
    }
    [Fact] public void RevokedOwnerAccessFailsClosedBeforeAnyMediaIsReturned()
    {
        _owner.SyncPlayAccess = SyncPlayUserAccessType.None;
        Assert.Throws<PartyException>(() => _media.Read(_party, "known-generation", "index.m3u8"));
        Assert.Empty(_store.Parties);
    }
    [Fact] public void DisposedMediaCannotBeRestartedByGuestPolling()
    {
        _media.Dispose(); _media.Dispose();
        Assert.Equal(503, Assert.Throws<PartyException>(() => _media.State(_party)).Status);
    }
    [Fact] public void GuestPollingIsBoundedWithoutInvalidatingOtherGuests()
    {
        for (var i = 0; i < 120; i++) _store.RateLimitGuest(_guest, "state", 120);
        Assert.Equal(429, Assert.Throws<PartyException>(() => _store.RateLimitGuest(_guest, "state", 120)).Status);
        _store.RateLimitGuest(new string('B', 64), "state", 120);
    }
    [Fact] public void ReadOnlyAccountCannotDriveGuestMediaThroughNormalSessionProgressReports()
    {
        _owner.SyncPlayAccess = SyncPlayUserAccessType.CreateAndJoinGroups; _friend.SyncPlayAccess = SyncPlayUserAccessType.CreateAndJoinGroups;
        _owner.Permissions.Add(new Permission(PermissionKind.EnableMediaPlayback, true)); _friend.Permissions.Add(new Permission(PermissionKind.EnableMediaPlayback, true));
        var sessions = Mock.Of<ISessionManager>();
        _sessions.Clear(); _sessions.Add(new SessionInfo(sessions, NullLogger.Instance) { Id = "friend", UserId = _friend.Id,
            NowPlayingItem = new MediaBrowser.Model.Dto.BaseItemDto { Id = Guid.NewGuid() } });
        _party.Members.Add("friend", _friend.Id); _store.RemoveSession("host"); _party.Account(_friend.Id).ControlPlayback = false;
        var state = _media.State(_party);
        Assert.False(state.Playing); Assert.Same(_party, _store.GuestParty(_guest, _config));
    }
    public void Dispose() => _media.Dispose();
}
