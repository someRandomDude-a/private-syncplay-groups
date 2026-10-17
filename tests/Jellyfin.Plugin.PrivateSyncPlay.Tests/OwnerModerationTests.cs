using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.SyncPlay;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class OwnerModerationTests : IDisposable
{
    private readonly PartyStore _store = new(TimeProvider.System);
    private readonly Configuration _config = new() { EnableGuestInvites = true, DefaultGuestPermission = true };
    private readonly Mock<ISyncPlayManager> _engine = new();
    private readonly Mock<ISessionManager> _sessions = new();
    private readonly List<SessionInfo> _active = [];
    private readonly SessionInfo _host, _friend, _second, _outsider;
    private readonly PrivateSyncPlayManager _manager;
    private readonly Party _party;
    public OwnerModerationTests()
    {
        _host = Session("owner", Guid.NewGuid()); _friend = Session("friend", Guid.NewGuid());
        _second = Session("friend-second-device", _friend.UserId); _outsider = Session("outsider", Guid.NewGuid());
        _sessions.SetupGet(s => s.Sessions).Returns(_active);
        _party = _store.Create(Guid.NewGuid(), _host.UserId, _host.Id, GroupMode.Private, 10);
        _party.Members.Add(_friend.Id, _friend.UserId); _party.Members.Add(_second.Id, _second.UserId); _party.Account(_friend.UserId);
        _engine.Setup(e => e.GetGroup(It.IsAny<SessionInfo>(), It.IsAny<Guid>())).Returns((SessionInfo _, Guid id) =>
            _store.Parties.ContainsKey(id) ? new GroupInfoDto(id, "Party", default, [], DateTime.UtcNow) : null!);
        _manager = new(new CoreEngine(_engine.Object), _store, _sessions.Object, () => _config);
    }
    private SessionInfo Session(string id, Guid user)
    { var session = new SessionInfo(_sessions.Object, NullLogger.Instance) { Id = id, UserId = user }; _active.Add(session); return session; }
    private string Invite() => _store.Issue(_party, _host.UserId, true, 10, null, _config).Token;
    private Guid FriendId => _party.Account(_friend.UserId).Id;
    private static IGroupPlaybackRequest Request(PlaybackRequestType action)
    { var request = new Mock<IGroupPlaybackRequest>(); request.SetupGet(r => r.Action).Returns(action); return request.Object; }
    public void Dispose() => _manager.Dispose();

    [Fact] public void ManagementRequiresTheOwnerAndMembershipInThatExactParty()
    {
        Assert.Equal(403, Assert.Throws<PartyException>(() => _manager.SetAccountPermissions(_friend, _party.Id, FriendId, false, false)).Status);
        Assert.Equal(404, Assert.Throws<PartyException>(() => _manager.Kick(_outsider, _party.Id, FriendId)).Status);
        Assert.Equal(404, Assert.Throws<PartyException>(() => _manager.UpdatePolicy(_host, Guid.NewGuid(), new(), 10)).Status);
        Assert.Equal(400, Assert.Throws<PartyException>(() => _manager.Kick(_host, _party.Id, _party.Account(_host.UserId).Id)).Status);
        Assert.Equal(400, Assert.Throws<PartyException>(() => _manager.SetAccountPermissions(_host, _party.Id, _party.Account(_host.UserId).Id, false, false)).Status);
    }
    [Theory]
    [InlineData(PlaybackRequestType.Pause)]
    [InlineData(PlaybackRequestType.Unpause)]
    [InlineData(PlaybackRequestType.Stop)]
    [InlineData(PlaybackRequestType.Seek)]
    [InlineData(PlaybackRequestType.NextItem)]
    [InlineData(PlaybackRequestType.PreviousItem)]
    [InlineData(PlaybackRequestType.SetPlaylistItem)]
    [InlineData(PlaybackRequestType.Play)]
    [InlineData(PlaybackRequestType.Queue)]
    [InlineData(PlaybackRequestType.RemoveFromPlaylist)]
    [InlineData(PlaybackRequestType.MovePlaylistItem)]
    [InlineData(PlaybackRequestType.SetRepeatMode)]
    [InlineData(PlaybackRequestType.SetShuffleMode)]
    public void ReadOnlyPermissionBlocksEveryNativeMutationForEveryDevice(PlaybackRequestType action)
    {
        _manager.SetAccountPermissions(_host, _party.Id, FriendId, false, true);
        var request = Request(action);
        Assert.Equal(403, Assert.Throws<PartyException>(() => _manager.HandleRequest(_friend, request, default)).Status);
        Assert.Equal(403, Assert.Throws<PartyException>(() => _manager.HandleRequest(_second, request, default)).Status);
        _engine.Verify(e => e.HandleRequest(It.IsAny<SessionInfo>(), request, It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact] public void BufferAndReadyReportsCannotBecomeAnIndirectPauseOrSeekBypass()
    {
        _manager.SetAccountPermissions(_host, _party.Id, FriendId, false, false);
        foreach (var action in new[] { PlaybackRequestType.Buffer, PlaybackRequestType.Ready, PlaybackRequestType.IgnoreWait })
        {
            var request = Request(action); _manager.HandleRequest(_friend, request, default);
            _engine.Verify(e => e.HandleRequest(_friend, request, It.IsAny<CancellationToken>()), Times.Never);
        }
        _engine.Verify(e => e.HandleRequest(_friend, It.Is<IgnoreWaitGroupRequest>(r => r.IgnoreWait), It.IsAny<CancellationToken>()), Times.Once);
        var ping = Request(PlaybackRequestType.Ping); _manager.HandleRequest(_friend, ping, default);
        _engine.Verify(e => e.HandleRequest(_friend, ping, It.IsAny<CancellationToken>()), Times.Once);
        _manager.SetAccountPermissions(_host, _party.Id, FriendId, true, true);
        _engine.Verify(e => e.HandleRequest(_friend, It.Is<IgnoreWaitGroupRequest>(r => !r.IgnoreWait), It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact] public void PartyWideCeilingsCannotBeBypassedByPerAccountAllowAndOwnerRetainsControl()
    {
        _manager.UpdatePolicy(_host, _party.Id, new(ParticipantsControlPlayback:false, ParticipantsEditQueue:false), 10);
        _manager.SetAccountPermissions(_host, _party.Id, FriendId, true, true);
        Assert.False(_party.CanControl(_friend.UserId)); Assert.False(_party.CanEditQueue(_friend.UserId));
        var request = Request(PlaybackRequestType.Play); _manager.HandleRequest(_host, request, default);
        _engine.Verify(e => e.HandleRequest(_host, request, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(403, Assert.Throws<PartyException>(() => _manager.HandleRequest(_host, Request((PlaybackRequestType)999), default)).Status);
    }
    [Fact] public void QueuePermissionsCanBeNarrowerThanTransportAndCannotRewriteTheCurrentItemIndirectly()
    {
        _manager.SetAccountPermissions(_host, _party.Id, FriendId, true, false);
        _manager.HandleRequest(_friend, Request(PlaybackRequestType.Pause), default);
        Assert.Equal(403, Assert.Throws<PartyException>(() => _manager.HandleRequest(_friend, Request(PlaybackRequestType.Play), default)).Status);
        Assert.Equal(403, Assert.Throws<PartyException>(() => _manager.HandleRequest(_friend, Request(PlaybackRequestType.RemoveFromPlaylist), default)).Status);
    }
    [Fact] public void AccountKickRevokesAllDevicesBeforeNativeCleanupAndBlocksCodeInviteAndNewDevices()
    {
        var invite = Invite();
        _engine.Setup(e => e.LeaveGroup(It.IsAny<SessionInfo>(), It.IsAny<LeaveGroupRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => { Assert.DoesNotContain(_friend.Id, _party.Members); Assert.DoesNotContain(_second.Id, _party.Members); });
        _manager.Kick(_host, _party.Id, FriendId);
        Assert.Same(_party, _store.ForSession(_host.Id, _host.UserId)); Assert.Contains(_friend.UserId, _party.BlockedUsers);
        var nextDevice = Session("fresh-device", _friend.UserId);
        Assert.Equal(403, Assert.Throws<PartyException>(() => _manager.JoinCode(nextDevice, _party.Code, default)).Status);
        Assert.Equal(403, Assert.Throws<PartyException>(() => _manager.JoinInvite(nextDevice, invite, default)).Status);
        Assert.Throws<PartyException>(() => _manager.HandleRequest(_friend, Request(PlaybackRequestType.Pause), default));
        Assert.Equal(0, _store.Invites(_party.Id).Single().Uses);
        _engine.Verify(e => e.LeaveGroup(It.IsAny<SessionInfo>(), It.IsAny<LeaveGroupRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _manager.Unblock(_host, _party.Id, FriendId); Assert.Null(_store.ForSession(nextDevice.Id, nextDevice.UserId));
        _manager.JoinCode(nextDevice, _party.Code, default); Assert.Contains(nextDevice.Id, _party.Members);
    }
    [Fact] public void GuestKickRevokesItsSharedLinkAndLeasesButNotUnrelatedInvitations()
    {
        var link = Invite(); var first = _store.RedeemGuest(link, null, _config); var second = _store.RedeemGuest(link, null, _config);
        var other = _store.RedeemGuest(Invite(), null, _config);
        _manager.Kick(_host, _party.Id, _store.GuestSession(first, _config).Id);
        Assert.Throws<PartyException>(() => _store.GuestParty(first, _config)); Assert.Throws<PartyException>(() => _store.GuestParty(second, _config));
        Assert.Throws<PartyException>(() => _store.RedeemGuest(link, null, _config)); Assert.Same(_party, _store.GuestParty(other, _config));
    }
    [Fact] public void AccountBlocksAlsoCoverPublicListsDirectJoinsAndNewDevicesWithoutBanningOtherParties()
    {
        var host = Session("public-host", Guid.NewGuid());
        var party = _store.Create(Guid.NewGuid(), host.UserId, host.Id, GroupMode.Public, 10);
        var dto = new GroupInfoDto(party.Id, "Public party", default, [], DateTime.UtcNow);
        _engine.Setup(e => e.ListGroups(It.IsAny<SessionInfo>(), It.IsAny<ListGroupsRequest>())).Returns([dto]);
        _manager.JoinGroup(_friend, new JoinGroupRequest(party.Id), default);
        _manager.Kick(host, party.Id, party.Account(_friend.UserId).Id);
        var device = Session("new-public-device", _friend.UserId);
        Assert.Empty(_manager.ListGroups(device, new ListGroupsRequest())); Assert.Null(_manager.GetGroup(device, party.Id));
        Assert.Throws<PartyException>(() => _manager.JoinGroup(device, new JoinGroupRequest(party.Id), default));
        _manager.JoinCode(device, _party.Code, default); Assert.Contains(device.Id, _party.Members);
    }
    [Fact] public void LockBlocksNewAccountsAndGuestsButRetainsCurrentLeasesAndMembers()
    {
        var link = Invite(); var guest = _store.RedeemGuest(link, null, _config);
        _manager.UpdatePolicy(_host, _party.Id, new(AllowJoins:false), 10);
        Assert.Throws<PartyException>(() => _manager.JoinCode(_outsider, _party.Code, default));
        Assert.Throws<PartyException>(() => _manager.JoinInvite(_outsider, link, default));
        Assert.Throws<PartyException>(() => _store.RedeemGuest(link, null, _config));
        Assert.Equal(guest, _store.RedeemGuest(link, guest, _config)); _manager.JoinCode(_friend, _party.Code, default);
    }
    [Fact] public void GuestDisableRevokesPermanentlyAndViewerLimitsCannotExceedTheAdministratorOrEvictSilently()
    {
        var link = Invite(); var guest = _store.RedeemGuest(link, null, _config);
        Assert.Throws<PartyException>(() => _manager.UpdatePolicy(_host, _party.Id, new(), 2));
        Assert.Throws<PartyException>(() => _manager.UpdatePolicy(_host, _party.Id, new(), 11));
        _manager.UpdatePolicy(_host, _party.Id, new(AllowGuests:false), 3);
        Assert.Throws<PartyException>(() => _store.GuestParty(guest, _config)); Assert.Throws<PartyException>(() => Invite());
        _manager.UpdatePolicy(_host, _party.Id, new(), 10);
        Assert.Throws<PartyException>(() => _store.GuestParty(guest, _config)); Assert.Throws<PartyException>(() => _store.ValidateInvite(link, _config));
    }
    [Fact] public async Task GuestOptionsAreAdministratorOnlyAndCaptionDenialIsEnforcedAtTheEndpoint()
    {
        var guest = _store.RedeemGuest(Invite(), null, _config);
        var secondGuest = _store.RedeemGuest(Invite(), null, _config);
        _config.GuestPlayer.Features = ["volume"];
        _manager.UpdatePolicy(_host, _party.Id, new(ParticipantsControlPlayback:false), 10);
        Assert.Equal(new[] { "volume" }, _store.GuestOptions(guest, _config).Features);
        Assert.Equal(_store.GuestOptions(guest, _config).Features, _store.GuestOptions(secondGuest, _config).Features);
        var context = new DefaultHttpContext(); context.Request.Scheme = "https"; context.Request.Host = new("party.example"); context.Request.Headers.Cookie = "PrivateSyncPlay.Guest=" + guest;
        var controller = new GuestController(_store, null!, () => _config) { ControllerContext = new() { HttpContext = context } };
        using var subtitles = new GuestSubtitles(Mock.Of<MediaBrowser.Controller.MediaEncoding.ISubtitleEncoder>(), TimeProvider.System);
        Assert.Equal(403, (await Assert.ThrowsAsync<PartyException>(() => controller.Subtitle("generation", 2, subtitles))).Status);
        _config.GuestPlayer.Features = ["volume", "subtitles"];
        Assert.True(_store.GuestOptions(guest, _config).Allows("subtitles"));
        Assert.True(_store.GuestOptions(secondGuest, _config).Allows("subtitles"));
        Assert.DoesNotContain(typeof(PrivateSyncPlayManager).GetMethods(), m => m.Name == "SetGuestPermissions");
        Assert.Null(typeof(PartyPolicy).GetProperty("GuestFeatures")); Assert.Null(typeof(Guest).GetProperty("Features"));
    }
    [Fact] public void NativeRemovalFailureEndsOnlyThisPartyAndRevokesItsGuestAccess()
    {
        var guest = _store.RedeemGuest(Invite(), null, _config);
        _engine.Setup(e => e.LeaveGroup(It.IsAny<SessionInfo>(), It.IsAny<LeaveGroupRequest>(), It.IsAny<CancellationToken>())).Throws(new IOException("Native cleanup failed"));
        Assert.Equal(503, Assert.Throws<PartyException>(() => _manager.Kick(_host, _party.Id, FriendId)).Status);
        Assert.False(_store.Parties.ContainsKey(_party.Id)); Assert.Throws<PartyException>(() => _store.GuestParty(guest, _config));
    }
    [Fact] public void NativeWaitPolicyFailureCannotLeaveAPartiallyEnforcedLiveParty()
    {
        _engine.Setup(e => e.HandleRequest(It.IsAny<SessionInfo>(), It.IsAny<IGroupPlaybackRequest>(), It.IsAny<CancellationToken>())).Throws(new IOException());
        Assert.Equal(503, Assert.Throws<PartyException>(() => _manager.SetAccountPermissions(_host, _party.Id, FriendId, false, false)).Status);
        Assert.False(_store.Parties.ContainsKey(_party.Id));
    }
}
