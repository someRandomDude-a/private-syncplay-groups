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
    public void Dispose() { _provider.Dispose(); _manager.Dispose(); }
}
