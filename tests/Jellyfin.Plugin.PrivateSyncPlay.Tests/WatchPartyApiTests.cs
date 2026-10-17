using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.PrivateSyncPlay.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class WatchPartyApiTests : IDisposable
{
    private readonly Configuration _config = new() { EnableGuestInvites = true, DefaultGuestPermission = true };
    private readonly PartyStore _store = new(TimeProvider.System);
    private readonly User _owner = new("owner", "auth", "reset");
    private readonly User _friend = new("friend", "auth", "reset");
    private readonly List<SessionInfo> _sessions = [];
    private readonly Mock<ISyncPlayManager> _core = new();
    private readonly PrivateSyncPlayManager _manager;
    private readonly WatchPartyService _service;
    private readonly ServiceProvider _provider;
    private readonly IWatchPartyService _client;
    private bool _mediaAllowed = true;

    [Fact] public async Task AccountSwitchOnAnExistingSessionDoesNotInheritTheDotNetApiParty()
    {
        var party = await Create();
        _sessions.Single(s => s.Id == "owner-session").UserId = _friend.Id;
        Assert.Null(await _client.GetCurrentPartyAsync(_friend.Id, "owner-session"));
        Assert.Equal(404, (await Assert.ThrowsAsync<WatchPartyException>(() => _client.GetManagementAsync(_friend.Id, "owner-session", party.PartyId))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<WatchPartyException>(() => _client.CreateInviteAsync(_friend.Id, "owner-session", party.PartyId,
            new CreateInviteOptions(new Uri("https://party.example"))))).StatusCode);
    }
    public WatchPartyApiTests()
    {
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => id == _owner.Id ? _owner : id == _friend.Id ? _friend : null);
        var sessions = new Mock<ISessionManager>(); sessions.SetupGet(s => s.Sessions).Returns(_sessions);
        _sessions.Add(new SessionInfo(sessions.Object, NullLogger.Instance) { Id = "owner-session", UserId = _owner.Id });
        _sessions.Add(new SessionInfo(sessions.Object, NullLogger.Instance) { Id = "friend-session", UserId = _friend.Id });
        var groups = new Dictionary<Guid, GroupInfoDto>();
        _core.Setup(c => c.NewGroup(It.IsAny<SessionInfo>(), It.IsAny<NewGroupRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SessionInfo _, NewGroupRequest request, CancellationToken _) =>
            { var g = new GroupInfoDto(Guid.NewGuid(), request.GroupName, default, [], DateTime.UtcNow); groups[g.GroupId] = g; return g; });
        _core.Setup(c => c.GetGroup(It.IsAny<SessionInfo>(), It.IsAny<Guid>())).Returns((SessionInfo _, Guid id) => _mediaAllowed ? groups.GetValueOrDefault(id)! : null!);
        _manager = new PrivateSyncPlayManager(new CoreEngine(_core.Object), _store, sessions.Object, () => _config);
        _service = new WatchPartyService(_manager, _store, users.Object, sessions.Object);
        var registration = new ServiceCollection();
        registration.AddKeyedSingleton<Func<Guid, string, string, string, CancellationToken, Task<string>>>(WatchPartyClient.ServiceKey, (_, _) => _service.InvokeAsync);
        registration.AddPrivateSyncPlayClient();
        _provider = registration.BuildServiceProvider(); _client = _provider.GetRequiredService<IWatchPartyService>();
    }
    private Task<WatchPartyInfo> Create() => _client.CreatePartyAsync(_owner.Id, "owner-session", new("Movie night", WatchPartyVisibility.Private, 3));
    private static CreateInviteOptions Invite(bool guests = false) => new(new Uri("https://media.example/jellyfin"), guests, 2);

    [Fact] public async Task TypedClientUsesVersionedBridgeAndPreservesBaseUrl()
    {
        Assert.True(_client.IsAvailable); Assert.IsType<WatchPartyClient>(_client);
        var result = await _client.CreatePartyWithInviteAsync(_owner.Id, "owner-session", new("Movie", WatchPartyVisibility.Private, 3), Invite(true));
        Assert.Equal("/jellyfin/PrivateSyncPlay/Watch", result.Invite.InviteUrl.AbsolutePath);
        Assert.Equal(65, result.Invite.InviteUrl.Fragment.Length);
        Assert.Equal(result.Party.PartyId, result.Invite.PartyId);
        Assert.Equal(1, result.Party.ViewerCount); Assert.Equal(8, result.Party.JoinCode!.Length);
    }
    [Fact] public async Task AbsentProviderHasDiscoverableAvailabilityAnd503Error()
    {
        using var provider = new ServiceCollection().AddPrivateSyncPlayClient().BuildServiceProvider();
        var client = provider.GetRequiredService<IWatchPartyService>(); Assert.False(client.IsAvailable);
        Assert.Equal(503, (await Assert.ThrowsAsync<WatchPartyException>(() => client.GetCurrentPartyAsync(_owner.Id, "owner-session"))).StatusCode);
    }
    [Fact] public async Task NoActiveSessionCannotCreateOrKeepPartyAlive()
    {
        _sessions.Clear();
        Assert.Equal(403, (await Assert.ThrowsAsync<WatchPartyException>(Create)).StatusCode);
        Assert.Empty(_store.Parties);
    }
    [Fact] public async Task ActorCannotUseAnotherUsersSession()
    {
        Assert.Equal(403, (await Assert.ThrowsAsync<WatchPartyException>(() => _client.CreatePartyAsync(_owner.Id, "friend-session", new("Bad")))).StatusCode);
        Assert.Empty(_store.Parties);
    }
    [Fact] public async Task NativeCreatePermissionAndDisabledAccountAreEnforced()
    {
        _owner.SyncPlayAccess = SyncPlayUserAccessType.JoinGroups;
        Assert.Equal(403, (await Assert.ThrowsAsync<WatchPartyException>(Create)).StatusCode);
        _owner.SyncPlayAccess = SyncPlayUserAccessType.CreateAndJoinGroups;
        _owner.Permissions.Add(new Permission(PermissionKind.IsDisabled, true));
        Assert.Equal(403, (await Assert.ThrowsAsync<WatchPartyException>(Create)).StatusCode);
    }
    [Fact] public async Task InvalidGuestInviteDoesNotCreateOrReplaceAParty()
    {
        _config.EnableGuestInvites = false;
        Assert.Equal(403, (await Assert.ThrowsAsync<WatchPartyException>(() => _client.CreatePartyWithInviteAsync(_owner.Id, "owner-session", new("Movie"), Invite(true)))).StatusCode);
        Assert.Empty(_store.Parties);
        _core.Verify(c => c.NewGroup(It.IsAny<SessionInfo>(), It.IsAny<NewGroupRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Theory]
    [InlineData("https://name:password@media.example/")]
    [InlineData("https://media.example/?token=secret")]
    [InlineData("https://media.example/#secret")]
    [InlineData("file:///server/config")]
    public async Task UnsafeBaseUrlsAreRejectedBeforeCreation(string url)
    {
        Assert.Equal(400, (await Assert.ThrowsAsync<WatchPartyException>(() => _client.CreatePartyWithInviteAsync(_owner.Id, "owner-session", new("Movie"), new(new Uri(url))))).StatusCode);
        Assert.Empty(_store.Parties);
    }
    [Fact] public async Task GuestHttpLinksAreRejected()
    {
        Assert.Equal(400, (await Assert.ThrowsAsync<WatchPartyException>(() => _client.CreatePartyWithInviteAsync(_owner.Id, "owner-session", new("Movie"), new(new Uri("http://media.example/"), true)))).StatusCode);
        Assert.Empty(_store.Parties);
    }
    [Fact] public async Task ExistingMembershipIsNotSilentlyReplaced()
    {
        var first = await Create();
        Assert.Equal(409, (await Assert.ThrowsAsync<WatchPartyException>(Create)).StatusCode);
        Assert.Equal(first.PartyId, (await _client.GetCurrentPartyAsync(_owner.Id, "owner-session"))!.PartyId);
    }
    [Fact] public async Task MemberCannotReadOwnerCodeOrCreateInvites()
    {
        var party = await Create();
        await _client.JoinByCodeAsync(_friend.Id, "friend-session", party.JoinCode!);
        Assert.Null((await _client.GetCurrentPartyAsync(_friend.Id, "friend-session"))!.JoinCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<WatchPartyException>(() => _client.CreateInviteAsync(_friend.Id, "friend-session", party.PartyId, Invite()))).StatusCode);
    }
    [Fact] public async Task MediaAccessIsNotDelegatedByJoinInvitation()
    {
        var party = await Create(); var invitation = await _client.CreateInviteAsync(_owner.Id, "owner-session", party.PartyId, Invite());
        _mediaAllowed = false;
        Assert.Equal(403, (await Assert.ThrowsAsync<WatchPartyException>(() => _client.JoinByInviteAsync(_friend.Id, "friend-session", invitation.InviteUrl.Fragment[1..]))).StatusCode);
        Assert.Null(await _client.GetCurrentPartyAsync(_friend.Id, "friend-session"));
    }
    [Fact] public async Task RevokeAndFinalMemberLeaveInvalidateGuestLeases()
    {
        var created = await _client.CreatePartyWithInviteAsync(_owner.Id, "owner-session", new("Movie"), Invite(true));
        string guest; lock (_store.Gate) guest = _store.RedeemGuest(created.Invite.InviteUrl.Fragment[1..], null, _config);
        await _client.RevokeInviteAsync(_owner.Id, "owner-session", created.Party.PartyId, created.Invite.InviteId);
        lock (_store.Gate) Assert.Throws<PartyException>(() => _store.GuestParty(guest, _config));
        await _client.LeavePartyAsync(_owner.Id, "owner-session"); Assert.Empty(_store.Parties);
    }
    [Fact] public async Task EndRequiresOwnerOrAdministrator()
    {
        var party = await Create();
        Assert.Equal(403, (await Assert.ThrowsAsync<WatchPartyException>(() => _client.EndPartyAsync(_friend.Id, "friend-session", party.PartyId))).StatusCode);
        _friend.Permissions.Add(new Permission(PermissionKind.IsAdministrator, true));
        await _client.EndPartyAsync(_friend.Id, "friend-session", party.PartyId); Assert.Empty(_store.Parties);
    }
    [Fact] public async Task CancellationDoesNotMutateState()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.CreatePartyAsync(_owner.Id, "owner-session", new("Movie"), cancellation.Token));
        Assert.Empty(_store.Parties);
    }
    [Fact] public async Task TheSingleCurrentApiIncludesPartyAndManagementOperations()
    {
        Assert.True(_client.IsAvailable); Assert.IsType<WatchPartyClient>(_client);
        var party = await Create();
        var snapshot = await _client.GetManagementAsync(_owner.Id, "owner-session", party.PartyId);
        Assert.Equal(party.PartyId, snapshot.PartyId); Assert.Single(snapshot.Accounts);
        Assert.Contains(typeof(IWatchPartyService).GetMethods(), m => m.Name == "GetManagementAsync");
        Assert.Contains(typeof(IWatchPartyService).GetMethods(), m => m.Name == "KickAccountAsync");
        Assert.Equal(typeof(IWatchPartyService), Assert.Single(typeof(IWatchPartyService).Assembly.GetTypes(), t => t.IsInterface));
    }
    [Fact] public async Task ManagementRejectsNonOwnersCrossPartyTargetsAndActorSessionSpoofing()
    {
        var party = await Create(); await _client.JoinByCodeAsync(_friend.Id, "friend-session", party.JoinCode!);
        Assert.Equal(403, (await Assert.ThrowsAsync<WatchPartyException>(() => _client.GetManagementAsync(_friend.Id, "friend-session", party.PartyId))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<WatchPartyException>(() => _client.GetManagementAsync(_owner.Id, "friend-session", party.PartyId))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<WatchPartyException>(() => _client.GetManagementAsync(_owner.Id, "owner-session", Guid.NewGuid()))).StatusCode);
        var snapshot = await _client.GetManagementAsync(_owner.Id, "owner-session", party.PartyId);
        Assert.Equal(403, (await Assert.ThrowsAsync<WatchPartyException>(() => _client.UpdatePolicyAsync(_friend.Id, "friend-session", party.PartyId, snapshot.Policy with { AllowJoins = false }))).StatusCode);
    }
    [Fact] public async Task TypedPolicyAndAccountPermissionsReachTheSameNativeRequestGuards()
    {
        var party = await Create(); await _client.JoinByCodeAsync(_friend.Id, "friend-session", party.JoinCode!);
        var snapshot = await _client.GetManagementAsync(_owner.Id, "owner-session", party.PartyId);
        var account = Assert.Single(snapshot.Accounts, a => !a.IsOwner); Assert.NotEqual(_friend.Id, account.ParticipantId);
        await _client.SetAccountPermissionsAsync(_owner.Id, "owner-session", party.PartyId, account.ParticipantId, new(false, true));
        Assert.Equal(403, Assert.Throws<PartyException>(() => _manager.HandleRequest(_sessions[1], new MediaBrowser.Controller.SyncPlay.PlaybackRequests.PauseGroupRequest(), default)).Status);
        snapshot = await _client.GetManagementAsync(_owner.Id, "owner-session", party.PartyId);
        Assert.False(snapshot.Accounts.Single(a => !a.IsOwner).EffectivePermissions.EditQueue);
        await _client.UpdatePolicyAsync(_owner.Id, "owner-session", party.PartyId, snapshot.Policy with { AllowJoins = false });
        _sessions.Add(new SessionInfo(Mock.Of<ISessionManager>(), NullLogger.Instance) { Id = "friend-new-device", UserId = _friend.Id });
        Assert.Equal(403, (await Assert.ThrowsAsync<WatchPartyException>(() => _client.JoinByCodeAsync(_friend.Id, "friend-new-device", party.JoinCode!))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<WatchPartyException>(() => _client.UpdatePolicyAsync(_owner.Id, "owner-session", party.PartyId, snapshot.Policy with { MaxViewers = 1001 }))).StatusCode);
    }
    [Fact] public async Task TypedOwnerApiCannotOverrideAdministratorGuestOptions()
    {
        var created = await _client.CreatePartyWithInviteAsync(_owner.Id, "owner-session", new("Guests"), Invite(true));
        var secret = _store.RedeemGuest(created.Invite.InviteUrl.Fragment[1..], null, _config);
        var snapshot = await _client.GetManagementAsync(_owner.Id, "owner-session", created.Party.PartyId);
        Assert.Single(snapshot.Guests);
        Assert.DoesNotContain(typeof(IWatchPartyService).GetMethods(), m => m.Name == "SetGuestPermissionsAsync");
        Assert.Null(typeof(WatchPartyPolicy).GetProperty("GuestFeatures"));
        var unknownOperation = await _service.InvokeAsync(_owner.Id, "owner-session", "SetGuestPermissions", "{}", default);
        Assert.Contains("400", unknownOperation);
        var payload = System.Text.Json.JsonSerializer.Serialize(new { partyId = created.Party.PartyId,
            policy = new { allowJoins = false, allowGuests = true, participantsControlPlayback = true, participantsEditQueue = true, maxViewers = 4, guestFeatures = new[] { "volume" } } });
        Assert.Contains("400", await _service.InvokeAsync(_owner.Id, "owner-session", "UpdatePolicy", payload, default));
        Assert.True(_store.Require(created.Party.PartyId).Policy.AllowJoins);
        _config.GuestPlayer.Features = [GuestPlayerFeatures.Volume];
        Assert.Equal(new[] { GuestPlayerFeatures.Volume }, _store.GuestOptions(secret, _config).Features);
    }
    [Fact] public async Task TypedAccountKickAndUnblockCoverAllDevicesAndRetainOverrides()
    {
        var party = await Create();
        _sessions.Add(new SessionInfo(Mock.Of<ISessionManager>(), NullLogger.Instance) { Id = "friend-second", UserId = _friend.Id });
        await _client.JoinByCodeAsync(_friend.Id, "friend-session", party.JoinCode!); await _client.JoinByCodeAsync(_friend.Id, "friend-second", party.JoinCode!);
        var account = (await _client.GetManagementAsync(_owner.Id, "owner-session", party.PartyId)).Accounts.Single(a => !a.IsOwner);
        Assert.Equal(2, account.SessionCount);
        await _client.SetAccountPermissionsAsync(_owner.Id, "owner-session", party.PartyId, account.ParticipantId, new(false, false));
        await _client.KickAccountAsync(_owner.Id, "owner-session", party.PartyId, account.ParticipantId);
        Assert.Null(await _client.GetCurrentPartyAsync(_friend.Id, "friend-session")); Assert.Null(await _client.GetCurrentPartyAsync(_friend.Id, "friend-second"));
        Assert.Equal(403, (await Assert.ThrowsAsync<WatchPartyException>(() => _client.JoinByCodeAsync(_friend.Id, "friend-session", party.JoinCode!))).StatusCode);
        var blocked = Assert.Single((await _client.GetManagementAsync(_owner.Id, "owner-session", party.PartyId)).BlockedAccounts);
        Assert.Equal(account.ParticipantId, blocked.ParticipantId);
        await _client.UnblockAccountAsync(_owner.Id, "owner-session", party.PartyId, blocked.ParticipantId);
        Assert.Null(await _client.GetCurrentPartyAsync(_friend.Id, "friend-session"));
        await _client.JoinByCodeAsync(_friend.Id, "friend-session", party.JoinCode!);
        Assert.False((await _client.GetManagementAsync(_owner.Id, "owner-session", party.PartyId)).Accounts.Single(a => !a.IsOwner).EffectivePermissions.ControlPlayback);
    }
    [Fact] public async Task GuestKickIsExplicitlyDifferentFromAccountKickAndRevokesTheSharedInvitation()
    {
        var created = await _client.CreatePartyWithInviteAsync(_owner.Id, "owner-session", new("Guests", MaxViewers:3), Invite(true));
        var token = created.Invite.InviteUrl.Fragment[1..]; var first = _store.RedeemGuest(token, null, _config); var second = _store.RedeemGuest(token, null, _config);
        var snapshot = await _client.GetManagementAsync(_owner.Id, "owner-session", created.Party.PartyId);
        var guest = snapshot.Guests[0]; Assert.Equal(2, guest.SharedInviteGuestCount);
        Assert.Equal(404, (await Assert.ThrowsAsync<WatchPartyException>(() => _client.KickAccountAsync(_owner.Id, "owner-session", created.Party.PartyId, guest.ParticipantId))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<WatchPartyException>(() => _client.KickGuestAndRevokeInviteAsync(_owner.Id, "owner-session", created.Party.PartyId, snapshot.Accounts[0].ParticipantId))).StatusCode);
        Assert.Same(_store.Require(created.Party.PartyId), _store.GuestParty(first, _config));
        await _client.KickGuestAndRevokeInviteAsync(_owner.Id, "owner-session", created.Party.PartyId, guest.ParticipantId);
        Assert.Throws<PartyException>(() => _store.GuestParty(first, _config)); Assert.Throws<PartyException>(() => _store.GuestParty(second, _config));
        Assert.Throws<PartyException>(() => _store.ValidateInvite(token, _config)); Assert.Single(_store.Parties);
    }
    [Fact] public async Task DirectAndClientSnapshotsNeverExposeMutableLiveStateOrCredentialFields()
    {
        var created = await _client.CreatePartyWithInviteAsync(_owner.Id, "owner-session", new("Guests"), Invite(true));
        var secret = _store.RedeemGuest(created.Invite.InviteUrl.Fragment[1..], null, _config);
        var party = _store.Require(created.Party.PartyId);
        _config.GuestPlayer.Features = [GuestPlayerFeatures.Volume, GuestPlayerFeatures.Subtitles];
        var snapshot = await _service.GetManagementAsync(_owner.Id, "owner-session", party.Id);
        ((string[])snapshot.GuestFeatures)[0] = "pause";
        Assert.Equal(GuestPlayerFeatures.Volume, _config.GuestPlayer.Features[0]);
        var json = System.Text.Json.JsonSerializer.Serialize(await _client.GetManagementAsync(_owner.Id, "owner-session", party.Id));
        Assert.DoesNotContain(secret, json); Assert.DoesNotContain(created.Invite.InviteUrl.Fragment[1..], json); Assert.DoesNotContain("owner-session", json);
    }
    [Fact] public async Task ManagementCancellationAndMalformedProtocolDoNotMutatePartyPolicy()
    {
        var party = await Create(); var snapshot = await _client.GetManagementAsync(_owner.Id, "owner-session", party.PartyId);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.UpdatePolicyAsync(_owner.Id, "owner-session", party.PartyId, snapshot.Policy with { AllowJoins = false }, cts.Token));
        Assert.True(_store.Require(party.PartyId).Policy.AllowJoins);
        foreach (var payload in new[] { "[]", "null", "not JSON", "{}" })
        {
            var reply = await _service.InvokeAsync(_owner.Id, "owner-session", "UpdatePolicy", payload, default);
            Assert.Contains("400", reply);
        }
    }
    public void Dispose() { _provider.Dispose(); _manager.Dispose(); }
}
