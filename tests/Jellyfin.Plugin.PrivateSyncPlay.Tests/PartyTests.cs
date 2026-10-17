using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class PartyTests
{
    private readonly ManualClock _clock = new();
    private readonly Configuration _config = new() { EnableGuestInvites = true, DefaultGuestPermission = true };
    private readonly Guid _owner = Guid.NewGuid();
    private readonly PartyStore _store;
    public PartyTests() => _store = new PartyStore(_clock);
    private Party Party(int seats = 10) => _store.Create(Guid.NewGuid(), _owner, "host", GroupMode.Private, seats);
    private string Invite(Party party, int uses = 10) => _store.Issue(party, _owner, true, uses, null, _config).Token;

    [Fact] public void CodesAreUniqueUppercaseEightCharacters()
    {
        var codes = Enumerable.Range(0, 500).Select(_ => Party().Code).ToArray();
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.All(codes, code => Assert.Matches("^[A-HJ-NP-Z2-9]{8}$", code));
    }
    [Fact] public void CodeNormalizationAndPublicCodeRejection()
    {
        var party = Party();
        Assert.Same(party, _store.ByCode(" " + party.Code.ToLowerInvariant() + " "));
        var pub = _store.Create(Guid.NewGuid(), _owner, "other", GroupMode.Public, 3);
        Assert.Equal(404, Assert.Throws<PartyException>(() => _store.ByCode(pub.Code)).Status);
    }
    [Fact] public void GuestNeverKeepsPartyAlive()
    {
        var party = Party(); var invite = Invite(party);
        var guest = _store.RedeemGuest(invite, null, _config);
        _store.RemoveSession("host");
        Assert.Empty(_store.Parties);
        Assert.Equal(401, Assert.Throws<PartyException>(() => _store.GuestParty(guest, _config, true)).Status);
        Assert.Throws<PartyException>(() => _store.RedeemGuest(invite, null, _config));
    }
    [Fact] public void OtherSignedInMemberCanKeepPartyAlive()
    {
        var party = Party(); party.Members.Add("friend", Guid.NewGuid());
        var guest = _store.RedeemGuest(Invite(party), null, _config);
        _store.RemoveSession("host");
        Assert.Same(party, _store.GuestParty(guest, _config));
        _store.RemoveSession("friend");
        Assert.Throws<PartyException>(() => _store.GuestParty(guest, _config));
    }
    [Fact] public void CapacityCountsHostAndGuestsAcrossInvites()
    {
        var party = Party(2);
        _store.RedeemGuest(Invite(party), null, _config);
        Assert.Equal(409, Assert.Throws<PartyException>(() => _store.RedeemGuest(Invite(party), null, _config)).Status);
        Assert.Throws<PartyException>(() => _store.CheckSeat(party, "new-account", _config));
        _store.CheckSeat(party, "host", _config);
    }
    [Fact] public void ConcurrentRedemptionsCannotOversubscribe()
    {
        var party = Party(4); var invite = Invite(party, 10); var admitted = 0;
        Parallel.For(0, 50, _ =>
        {
            lock (_store.Gate)
            {
                try { _store.RedeemGuest(invite, null, _config); admitted++; }
                catch (PartyException) { }
            }
        });
        Assert.Equal(3, admitted);
    }
    [Fact] public void RefreshDoesNotConsumeAnotherUseOrSeat()
    {
        var party = Party(2); var invite = Invite(party, 1);
        var guest = _store.RedeemGuest(invite, null, _config);
        Assert.Equal(guest, _store.RedeemGuest(invite, guest, _config));
        Assert.Equal(1, _store.Invites(party.Id).Single().Uses);
        Assert.Equal(1, _store.GuestCount(party.Id));
        Assert.Throws<PartyException>(() => _store.RedeemGuest(invite, null, _config));
    }
    [Fact] public void ExpiredLeaseReleasesSeatButNotRedemption()
    {
        var party = Party(2); var invite = Invite(party, 2);
        var first = _store.RedeemGuest(invite, null, _config);
        _clock.Advance(31);
        Assert.Throws<PartyException>(() => _store.GuestParty(first, _config));
        _store.RedeemGuest(invite, null, _config);
        Assert.Equal(2, _store.Invites(party.Id).Single().Uses);
    }
    [Fact] public void InvitationOptionsConsumeNoSeatsOrUsesAndDoNotExtendAnExistingLease()
    {
        var party = Party(2); var invite = Invite(party, 1);
        for (var i = 0; i < 5; i++) Assert.True(_store.GuestAdmissionAllowed(invite, null, _config));
        Assert.Empty(_store.Guests(party.Id)); Assert.Equal(0, _store.Invites(party.Id).Single().Uses);
        var guest = _store.RedeemGuest(invite, null, _config);
        _clock.Advance(20);
        Assert.True(_store.GuestAdmissionAllowed(invite, guest, _config));
        Assert.Equal(1, _store.Invites(party.Id).Single().Uses);
        _clock.Advance(11);
        Assert.Throws<PartyException>(() => _store.GuestParty(guest, _config));
    }
    [Fact] public void InvitationOptionsRespectAccountOnlyLockedFullAndRevokedAdmissions()
    {
        var party = Party(2); var invite = Invite(party);
        var account = _store.Issue(party, _owner, false, 1, null, _config).Token;
        Assert.False(_store.GuestAdmissionAllowed(account, null, _config));
        party.Policy = party.Policy with { AllowJoins = false };
        Assert.False(_store.GuestAdmissionAllowed(invite, null, _config));
        party.Policy = party.Policy with { AllowJoins = true };
        var guest = _store.RedeemGuest(invite, null, _config);
        Assert.False(_store.GuestAdmissionAllowed(invite, null, _config));
        Assert.True(_store.GuestAdmissionAllowed(invite, guest, _config));
        _config.EnableGuestInvites = false;
        Assert.Throws<PartyException>(() => _store.GuestAdmissionAllowed(invite, guest, _config));
        Assert.False(_store.GuestAdmissionAllowed(account, null, _config));
    }
    [Fact] public void MediaRequestsDoNotExtendLease()
    {
        var party = Party(); var guest = _store.RedeemGuest(Invite(party), null, _config);
        _clock.Advance(20); _store.GuestParty(guest, _config);
        _clock.Advance(11);
        Assert.Throws<PartyException>(() => _store.GuestParty(guest, _config));
    }
    [Fact] public void HeartbeatExtendsLease()
    {
        var party = Party(); var guest = _store.RedeemGuest(Invite(party), null, _config);
        _clock.Advance(20); _store.GuestParty(guest, _config, true);
        _clock.Advance(20); _store.GuestParty(guest, _config, true);
    }
    [Fact] public void ServerDisableOverridesUserAllowAndRevokesPermanently()
    {
        _config.Users.Add(new UserSettings { UserId = _owner, GuestInvites = PermissionOverride.Allow });
        var party = Party(); var guest = _store.RedeemGuest(Invite(party), null, _config);
        _config.EnableGuestInvites = false; _store.Sweep(_config);
        Assert.False(_config.CanInviteGuests(_owner));
        _config.EnableGuestInvites = true;
        Assert.Throws<PartyException>(() => _store.GuestParty(guest, _config));
    }
    [Fact] public void PerUserDenyRevokesOnlyThatOwnersGuests()
    {
        var party = Party(); var guest = _store.RedeemGuest(Invite(party), null, _config);
        _config.Users.Add(new UserSettings { UserId = _owner, GuestInvites = PermissionOverride.Deny });
        Assert.Throws<PartyException>(() => _store.GuestParty(guest, _config));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RestoringGuestPermissionDoesNotResurrectPreviouslyIssuedGuestLinks(bool serverWide)
    {
        var party = Party(); var secret = Invite(party);
        var accountLink = _store.Issue(party, _owner, false, 2, null, _config).Token;
        var guest = _store.RedeemGuest(secret, null, _config);
        var other = _store.Create(Guid.NewGuid(), Guid.NewGuid(), "other", GroupMode.Private, 3);
        var otherLink = _store.Issue(other, other.Owner, true, 2, null, _config).Token;
        if (serverWide) _config.EnableGuestInvites = false;
        else _config.Users.Add(new UserSettings { UserId = _owner, GuestInvites = PermissionOverride.Deny });
        _store.Sweep(_config);
        _config.EnableGuestInvites = true; _config.Users.Clear();
        Assert.Throws<PartyException>(() => _store.RedeemGuest(secret, null, _config));
        Assert.Throws<PartyException>(() => _store.GuestParty(guest, _config));
        Assert.NotNull(_store.ValidateInvite(accountLink, _config));
        if (!serverWide) Assert.NotNull(_store.ValidateInvite(otherLink, _config));
    }

    [Fact] public void ReassignedSessionCannotInheritPrivateMembershipOrRefreshAnExhaustedInvitation()
    {
        var party = Party(); var secret = Invite(party, 1);
        var engine = new Mock<ISyncPlayManager>();
        var sessions = Mock.Of<ISessionManager>();
        var admitted = new SessionInfo(sessions, NullLogger.Instance) { Id = "reused-device", UserId = Guid.NewGuid() };
        engine.Setup(e => e.GetGroup(admitted, party.Id)).Returns(new GroupInfoDto(party.Id, "Party", default, [], DateTime.UtcNow));
        using var manager = new PrivateSyncPlayManager(new CoreEngine(engine.Object), _store, sessions, () => _config);
        manager.JoinInvite(admitted, secret, default);
        var previousUser = admitted.UserId;
        admitted.UserId = Guid.NewGuid();
        Assert.Null(manager.GetGroup(admitted, party.Id));
        Assert.Null(_store.ForSession(admitted.Id, admitted.UserId));
        Assert.NotNull(_store.ForSession(admitted.Id, previousUser));
        Assert.Throws<PartyException>(() => manager.JoinGroup(admitted, new JoinGroupRequest(party.Id), default));
        Assert.Throws<PartyException>(() => manager.HandleRequest(admitted, new MediaBrowser.Controller.SyncPlay.PlaybackRequests.PauseGroupRequest(), default));
        Assert.Throws<PartyException>(() => manager.OwnedParty(admitted, party.Id));
        Assert.Throws<PartyException>(() => manager.JoinInvite(admitted, secret, default));
        engine.Verify(e => e.JoinGroup(It.IsAny<SessionInfo>(), It.IsAny<JoinGroupRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        engine.Verify(e => e.HandleRequest(It.IsAny<SessionInfo>(), It.IsAny<IGroupPlaybackRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact] public void ReassignedSessionMustRedeemANewSeatAndInvitationUse()
    {
        var party = Party(3); var secret = Invite(party, 3);
        var engine = new Mock<ISyncPlayManager>(); var sessions = Mock.Of<ISessionManager>();
        var device = new SessionInfo(sessions, NullLogger.Instance) { Id = "reused-device", UserId = Guid.NewGuid() };
        engine.Setup(e => e.GetGroup(device, party.Id)).Returns(new GroupInfoDto(party.Id, "Party", default, [], DateTime.UtcNow));
        using var manager = new PrivateSyncPlayManager(new CoreEngine(engine.Object), _store, sessions, () => _config);
        manager.JoinInvite(device, secret, default); device.UserId = Guid.NewGuid();
        manager.JoinInvite(device, secret, default);
        Assert.True(party.IsMember(device.Id, device.UserId));
        Assert.Equal(2, _store.Invites(party.Id).Single().Uses); Assert.Equal(2, party.Members.Count);
    }
    [Fact] public void RevokeDisconnectsExistingGuests()
    {
        var party = Party(); var invite = Invite(party);
        var guest = _store.RedeemGuest(invite, null, _config);
        _store.Revoke(party, _store.Invites(party.Id).Single().Id);
        Assert.Throws<PartyException>(() => _store.GuestParty(guest, _config));
        Assert.Throws<PartyException>(() => _store.RedeemGuest(invite, null, _config));
    }
    [Fact] public void ExpirationRevokesGuestsAndLinks()
    {
        var party = Party(); var invite = _store.Issue(party, _owner, true, 3, _clock.GetUtcNow().AddSeconds(5), _config).Token;
        var guest = _store.RedeemGuest(invite, null, _config);
        _clock.Advance(6);
        Assert.Throws<PartyException>(() => _store.GuestParty(guest, _config));
        Assert.Throws<PartyException>(() => _store.ValidateInvite(invite, _config));
    }
    [Fact] public void NonOwnerCannotCreateInvites()
    {
        Assert.Equal(403, Assert.Throws<PartyException>(() => _store.Issue(Party(), Guid.NewGuid(), true, 1, null, _config)).Status);
    }
    [Fact] public void AccountOnlyInvitationCannotBeRedeemedByGuest()
    {
        var party = Party(); var invite = _store.Issue(party, _owner, false, 1, null, _config).Token;
        Assert.Equal(403, Assert.Throws<PartyException>(() => _store.RedeemGuest(invite, null, _config)).Status);
    }
    [Fact] public void GuestSessionSecretCannotBeUsedAsInvitation()
    {
        var party = Party(); var guest = _store.RedeemGuest(Invite(party), null, _config);
        Assert.Throws<PartyException>(() => _store.ValidateInvite(guest, _config));
        Assert.Throws<PartyException>(() => _store.GuestParty(Invite(party), _config));
    }
    [Fact] public void RateLimitResetsAfterWindow()
    {
        for (int i = 0; i < 20; i++) _store.RateLimit("user");
        Assert.Equal(429, Assert.Throws<PartyException>(() => _store.RateLimit("user")).Status);
        _clock.Advance(61); _store.RateLimit("user");
    }
    [Fact] public void UserDefaultsInheritAndCannotBypassServerRestriction()
    {
        _config.DefaultMode = GroupMode.Private;
        Assert.Equal(GroupMode.Private, _config.DefaultFor(_owner));
        _config.Users.Add(new UserSettings { UserId = _owner, DefaultMode = GroupMode.Public, CreatePrivate = PermissionOverride.Allow });
        Assert.Equal(GroupMode.Public, _config.DefaultFor(_owner));
        _config.EnablePrivateGroups = false;
        Assert.False(_config.CanCreate(_owner, GroupMode.Private));
    }
    [Fact] public void InvalidConfigurationIsRejected()
    {
        _config.MaxViewers = 0;
        Assert.Throws<ArgumentException>(_config.Validate);
    }

    [Fact] public void LoweredAdminInviteLimitConstrainsExistingLinks()
    {
        var party = Party(); var invite = Invite(party, 10);
        _store.RedeemGuest(invite, null, _config);
        _config.MaxInviteUses = 1;
        Assert.Throws<PartyException>(() => _store.RedeemGuest(invite, null, _config));
    }

    [Fact] public void MediaDenialDoesNotConsumeAnInviteOrMoveMembership()
    {
        var party = Party(); var secret = Invite(party);
        var engine = new Mock<ISyncPlayManager>();
        var sessions = Mock.Of<ISessionManager>();
        using var manager = new PrivateSyncPlayManager(new CoreEngine(engine.Object), _store, sessions, () => _config);
        var session = new SessionInfo(sessions, NullLogger.Instance) { Id = "outsider", UserId = Guid.NewGuid() };
        Assert.Equal(403, Assert.Throws<PartyException>(() => manager.JoinInvite(session, secret, default)).Status);
        Assert.Equal(0, _store.Invites(party.Id).Single().Uses);
        Assert.DoesNotContain("outsider", party.Members);
        engine.Verify(x => x.JoinGroup(It.IsAny<SessionInfo>(), It.IsAny<JoinGroupRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact] public void CoreListDetailAndDirectJoinCannotExposePrivateGroups()
    {
        var engine = new Mock<ISyncPlayManager>();
        var sessions = new Mock<ISessionManager>();
        var all = new List<GroupInfoDto>();
        engine.Setup(x => x.NewGroup(It.IsAny<SessionInfo>(), It.IsAny<NewGroupRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SessionInfo _, NewGroupRequest r, CancellationToken _) =>
            { var dto = new GroupInfoDto(Guid.NewGuid(), r.GroupName, default, [], DateTime.UtcNow); all.Add(dto); return dto; });
        engine.Setup(x => x.ListGroups(It.IsAny<SessionInfo>(), It.IsAny<ListGroupsRequest>())).Returns(all);
        engine.Setup(x => x.GetGroup(It.IsAny<SessionInfo>(), It.IsAny<Guid>())).Returns((SessionInfo _, Guid id) => all.FirstOrDefault(x => x.GroupId == id)!);
        using var manager = new PrivateSyncPlayManager(new CoreEngine(engine.Object), _store, sessions.Object, () => _config);
        SessionInfo Session(string id) => new(sessions.Object, NullLogger.Instance) { Id = id, UserId = Guid.NewGuid() };
        var host = Session("host"); var other = Session("public-host"); var outsider = Session("outsider");
        var priv = manager.Create(host, new NewGroupRequest("secret"), GroupMode.Private, 2, default);
        var pub = manager.Create(other, new NewGroupRequest("public"), GroupMode.Public, 2, default);
        Assert.Equal(pub.GroupId, Assert.Single(manager.ListGroups(outsider, new ListGroupsRequest())).GroupId);
        Assert.Null(manager.GetGroup(outsider, priv.GroupId));
        Assert.NotNull(manager.GetGroup(host, priv.GroupId));
        Assert.Throws<PartyException>(() => manager.JoinGroup(outsider, new JoinGroupRequest(priv.GroupId), default));
        engine.Verify(x => x.JoinGroup(It.IsAny<SessionInfo>(), It.IsAny<JoinGroupRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        manager.JoinCode(outsider, _store.Require(priv.GroupId).Code, default);
        Assert.NotNull(manager.GetGroup(outsider, priv.GroupId));
        Assert.Equal(2, _store.Require(priv.GroupId).Members.Count);
        var third = Session("third");
        Assert.Throws<PartyException>(() => manager.JoinCode(third, _store.Require(priv.GroupId).Code, default));
        manager.LeaveGroup(host, new LeaveGroupRequest(), default);
        manager.LeaveGroup(outsider, new LeaveGroupRequest(), default);
        Assert.False(_store.Parties.ContainsKey(priv.GroupId));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-16T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
}
