using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.PrivateSyncPlay.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay.Requests;

namespace Jellyfin.Plugin.PrivateSyncPlay;

/// <summary>Policy-enforcing implementation shared by internal plugin integrations.</summary>
public sealed class WatchPartyService(PrivateSyncPlayManager manager, PartyStore store, IUserManager users, ISessionManager sessions) : IWatchPartyService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    /// <inheritdoc />
    public bool IsAvailable => manager.Status.IsAvailable;

    private (User User, SessionInfo Session) Actor(Guid userId, string sessionId)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(sessionId)) throw new PartyException(403, "An authenticated user and active session are required.");
        var user = users.GetUserById(userId);
        if (user is null || user.Permissions.Any(p => p.Kind == PermissionKind.IsDisabled && p.Value)
            || user.SyncPlayAccess == SyncPlayUserAccessType.None)
            throw new PartyException(403, "An enabled user with SyncPlay access is required.");
        var session = sessions.Sessions.FirstOrDefault(s => s.UserId == userId && string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(sessionId) || session is null) throw new PartyException(403, "An active session belonging to this user is required.");
        return (user, session);
    }
    private Task<T> Run<T>(Func<T> action, CancellationToken token)
    {
        try { token.ThrowIfCancellationRequested(); lock (store.Gate) { token.ThrowIfCancellationRequested(); store.Sweep(manager.Config); return Task.FromResult(action()); } }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return Task.FromCanceled<T>(token); }
        catch (PartyException ex) { return Task.FromException<T>(new WatchPartyException(ex.Status, ex.Message)); }
        catch (ArgumentException) { return Task.FromException<T>(new WatchPartyException(400, "Invalid watch-party options.")); }
        catch (InvalidOperationException) { return Task.FromException<T>(new WatchPartyException(503, "Watch-party provider is not ready.")); }
    }
    private WatchPartyInfo Info(Party party, Guid actor) => new(party.Id, party.Owner,
        party.Mode == GroupMode.Private ? WatchPartyVisibility.Private : WatchPartyVisibility.Public,
        Math.Min(party.Capacity, manager.Config.MaxViewers), party.Members.Count + store.GuestCount(party.Id),
        party.Mode == GroupMode.Private && party.Owner == actor ? party.Code : null);
    private Party OwnedParty(Guid id, User user, SessionInfo session)
    {
        var party = store.ForSession(session.Id);
        if (party?.Id != id) throw new PartyException(404, "Party not found for this session.");
        if (party.Owner != user.Id) throw new PartyException(403, "Only the owner may manage invitations.");
        return party;
    }
    private Party Create(User user, SessionInfo session, CreatePartyOptions options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (user.SyncPlayAccess != SyncPlayUserAccessType.CreateAndJoinGroups) throw new PartyException(403, "Group creation is not permitted for this user.");
        if (store.ForSession(session.Id) is not null) throw new PartyException(409, "This session already belongs to a party. Reuse it or leave it explicitly.");
        if (options.Visibility.HasValue && !Enum.IsDefined(options.Visibility.Value)) throw new PartyException(400, "Invalid visibility.");
        var mode = options.Visibility switch { WatchPartyVisibility.Public => GroupMode.Public, WatchPartyVisibility.Private => GroupMode.Private, _ => (GroupMode?)null };
        var group = manager.Create(session, new NewGroupRequest(options.Name), mode, options.MaxViewers, token);
        return store.Require(group.GroupId);
    }
    private (Uri BaseUrl, int Uses) ValidateInvite(User user, CreateInviteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var url = options.ServerBaseUrl;
        if (url is null || !url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(url.Host) || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new PartyException(400, "Provide the trusted public HTTP(S) server base URL without credentials, query or fragment.");
        if (options.AllowGuests && url.Scheme != Uri.UriSchemeHttps) throw new PartyException(400, "Guest invitations require an HTTPS server URL.");
        if (options.AllowGuests && !manager.Config.CanInviteGuests(user.Id)) throw new PartyException(403, "Guest invitations are disabled by your administrator.");
        var uses = options.MaxUses ?? manager.Config.MaxInviteUses;
        if (uses < 1 || uses > manager.Config.MaxInviteUses || options.ExpiresAt <= store.Now) throw new PartyException(400, "Invalid invitation limits.");
        return (new Uri(url.AbsoluteUri.TrimEnd('/') + "/"), uses);
    }
    private WatchPartyInvite Issue(Party party, User user, CreateInviteOptions options)
    {
        var validated = ValidateInvite(user, options);
        var issued = store.Issue(party, user.Id, options.AllowGuests, validated.Uses, options.ExpiresAt, manager.Config);
        var url = new UriBuilder(new Uri(validated.BaseUrl, "PrivateSyncPlay/Watch")) { Fragment = issued.Token }.Uri;
        return new WatchPartyInvite(issued.Value.Id, party.Id, url, options.AllowGuests, issued.Value.MaxUses, options.ExpiresAt);
    }
    /// <inheritdoc />
    public Task<WatchPartyInfo?> GetCurrentPartyAsync(Guid userId, string sessionId, CancellationToken cancellationToken = default)
        => Run(() => { var actor = Actor(userId, sessionId); var p = store.ForSession(actor.Session.Id); return p is null ? null : Info(p, userId); }, cancellationToken);
    /// <inheritdoc />
    public Task<WatchPartyInfo> CreatePartyAsync(Guid userId, string sessionId, CreatePartyOptions options, CancellationToken cancellationToken = default)
        => Run(() => { var actor = Actor(userId, sessionId); return Info(Create(actor.User, actor.Session, options, cancellationToken), userId); }, cancellationToken);
    /// <inheritdoc />
    public Task<WatchPartyWithInvite> CreatePartyWithInviteAsync(Guid userId, string sessionId, CreatePartyOptions party, CreateInviteOptions invite, CancellationToken cancellationToken = default)
        => Run(() =>
        {
            var actor = Actor(userId, sessionId);
            ValidateInvite(actor.User, invite); // Never create a group for a disallowed/invalid invite.
            var created = Create(actor.User, actor.Session, party, cancellationToken);
            try { return new WatchPartyWithInvite(Info(created, userId), Issue(created, actor.User, invite)); }
            catch { manager.EndParty(created.Id); throw; }
        }, cancellationToken);
    /// <inheritdoc />
    public Task<WatchPartyInvite> CreateInviteAsync(Guid userId, string sessionId, Guid partyId, CreateInviteOptions options, CancellationToken cancellationToken = default)
        => Run(() => { var actor = Actor(userId, sessionId); return Issue(OwnedParty(partyId, actor.User, actor.Session), actor.User, options); }, cancellationToken);
    /// <inheritdoc />
    public Task RevokeInviteAsync(Guid userId, string sessionId, Guid partyId, Guid inviteId, CancellationToken cancellationToken = default)
        => Run(() => { var actor = Actor(userId, sessionId); store.Revoke(OwnedParty(partyId, actor.User, actor.Session), inviteId); return true; }, cancellationToken);
    /// <inheritdoc />
    public Task JoinByCodeAsync(Guid userId, string sessionId, string code, CancellationToken cancellationToken = default)
        => Run(() => { var actor = Actor(userId, sessionId); if (string.IsNullOrWhiteSpace(code) || code.Length > 16) throw new PartyException(400, "Invalid code."); manager.JoinCode(actor.Session, code, cancellationToken); return true; }, cancellationToken);
    /// <inheritdoc />
    public Task JoinByInviteAsync(Guid userId, string sessionId, string token, CancellationToken cancellationToken = default)
        => Run(() => { var actor = Actor(userId, sessionId); if (token?.Length != 64) throw new PartyException(400, "Invalid invitation."); manager.JoinInvite(actor.Session, token, cancellationToken); return true; }, cancellationToken);
    /// <inheritdoc />
    public Task LeavePartyAsync(Guid userId, string sessionId, CancellationToken cancellationToken = default)
        => Run(() => { var actor = Actor(userId, sessionId); manager.LeaveGroup(actor.Session, new LeaveGroupRequest(), cancellationToken); return true; }, cancellationToken);
    /// <inheritdoc />
    public Task EndPartyAsync(Guid userId, string sessionId, Guid partyId, CancellationToken cancellationToken = default)
        => Run(() =>
        {
            var actor = Actor(userId, sessionId); var p = store.Require(partyId);
            if (p.Owner != userId && !actor.User.Permissions.Any(x => x.Kind == PermissionKind.IsAdministrator && x.Value)) throw new PartyException(403, "Only the owner or administrator may end this party.");
            manager.EndParty(partyId); return true;
        }, cancellationToken);

    /// <inheritdoc />
    public Task<WatchPartyManagement> GetManagementAsync(Guid userId, string sessionId, Guid partyId, CancellationToken cancellationToken = default)
        => Run(() => { var actor = Actor(userId, sessionId); var party = manager.OwnedParty(actor.Session, partyId);
            return PartyManagementSnapshot.Capture(party, store, sessions, users, manager.Config); }, cancellationToken);
    /// <inheritdoc />
    public Task UpdatePolicyAsync(Guid userId, string sessionId, Guid partyId, WatchPartyPolicy policy, CancellationToken cancellationToken = default)
        => Run(() => { var actor = Actor(userId, sessionId); ArgumentNullException.ThrowIfNull(policy);
            manager.UpdatePolicy(actor.Session, partyId, new(policy.AllowJoins, policy.AllowGuests, policy.ParticipantsControlPlayback, policy.ParticipantsEditQueue, policy.GuestFeatures?.ToArray()), policy.MaxViewers); return true; }, cancellationToken);
    /// <inheritdoc />
    public Task SetAccountPermissionsAsync(Guid userId, string sessionId, Guid partyId, Guid participantId, WatchPartyAccountPermissions permissions, CancellationToken cancellationToken = default)
        => Run(() => { var actor = Actor(userId, sessionId); ArgumentNullException.ThrowIfNull(permissions);
            manager.SetAccountPermissions(actor.Session, partyId, participantId, permissions.ControlPlayback, permissions.EditQueue); return true; }, cancellationToken);
    /// <inheritdoc />
    public Task SetGuestPermissionsAsync(Guid userId, string sessionId, Guid partyId, Guid participantId, WatchPartyGuestPermissions options, CancellationToken cancellationToken = default)
        => Run(() => { var actor = Actor(userId, sessionId); ArgumentNullException.ThrowIfNull(options);
            manager.SetGuestPermissions(actor.Session, partyId, participantId, options.Features?.ToArray()); return true; }, cancellationToken);
    /// <inheritdoc />
    public Task KickAccountAsync(Guid userId, string sessionId, Guid partyId, Guid participantId, CancellationToken cancellationToken = default)
        => Run(() => { var actor = Actor(userId, sessionId); var party = manager.OwnedParty(actor.Session, partyId);
            if (!party.Accounts.Values.Any(a => a.Id == participantId)) throw new PartyException(404, "Account participant not found.");
            manager.Kick(actor.Session, partyId, participantId); return true; }, cancellationToken);
    /// <inheritdoc />
    public Task KickGuestAndRevokeInviteAsync(Guid userId, string sessionId, Guid partyId, Guid participantId, CancellationToken cancellationToken = default)
        => Run(() => { var actor = Actor(userId, sessionId); var party = manager.OwnedParty(actor.Session, partyId);
            if (!store.Guests(party.Id).Any(g => g.Id == participantId)) throw new PartyException(404, "Guest participant not found.");
            manager.Kick(actor.Session, partyId, participantId); return true; }, cancellationToken);
    /// <inheritdoc />
    public Task UnblockAccountAsync(Guid userId, string sessionId, Guid partyId, Guid participantId, CancellationToken cancellationToken = default)
        => Run(() => { var actor = Actor(userId, sessionId); manager.Unblock(actor.Session, partyId, participantId); return true; }, cancellationToken);

    /// <summary>Framework-only protocol entry point, avoiding SDK type identity across plugin load contexts.</summary>
    public async Task<string> InvokeAsync(Guid userId, string sessionId, string operation, string requestJson, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (requestJson is null || requestJson.Length > 65536) throw new WatchPartyException(400, "Request is missing or too large.");
            using var json = JsonDocument.Parse(requestJson); var r = json.RootElement;
            if (r.ValueKind != JsonValueKind.Object) throw new WatchPartyException(400, "Request options must be a JSON object.");
            T Read<T>(JsonElement value) => value.Deserialize<T>(Json) ?? throw new WatchPartyException(400, "Options are required.");
            object? result;
            switch (operation)
            {
                case "GetCurrentParty": result = await GetCurrentPartyAsync(userId, sessionId, token); break;
                case "CreateParty": result = await CreatePartyAsync(userId, sessionId, Read<CreatePartyOptions>(r), token); break;
                case "CreatePartyWithInvite": result = await CreatePartyWithInviteAsync(userId, sessionId, Read<CreatePartyOptions>(r.GetProperty("party")), Read<CreateInviteOptions>(r.GetProperty("invite")), token); break;
                case "CreateInvite": result = await CreateInviteAsync(userId, sessionId, r.GetProperty("partyId").GetGuid(), Read<CreateInviteOptions>(r.GetProperty("options")), token); break;
                case "RevokeInvite": await RevokeInviteAsync(userId, sessionId, r.GetProperty("partyId").GetGuid(), r.GetProperty("inviteId").GetGuid(), token); result = null; break;
                case "JoinByCode": await JoinByCodeAsync(userId, sessionId, r.GetProperty("code").GetString()!, token); result = null; break;
                case "JoinByInvite": await JoinByInviteAsync(userId, sessionId, r.GetProperty("token").GetString()!, token); result = null; break;
                case "LeaveParty": await LeavePartyAsync(userId, sessionId, token); result = null; break;
                case "EndParty": await EndPartyAsync(userId, sessionId, r.GetProperty("partyId").GetGuid(), token); result = null; break;
                case "GetManagement": result = await GetManagementAsync(userId, sessionId, r.GetProperty("partyId").GetGuid(), token); break;
                case "UpdatePolicy": await UpdatePolicyAsync(userId, sessionId, r.GetProperty("partyId").GetGuid(), Read<WatchPartyPolicy>(r.GetProperty("policy")), token); result = null; break;
                case "SetAccountPermissions": await SetAccountPermissionsAsync(userId, sessionId, r.GetProperty("partyId").GetGuid(), r.GetProperty("participantId").GetGuid(), Read<WatchPartyAccountPermissions>(r.GetProperty("permissions")), token); result = null; break;
                case "SetGuestPermissions": await SetGuestPermissionsAsync(userId, sessionId, r.GetProperty("partyId").GetGuid(), r.GetProperty("participantId").GetGuid(), Read<WatchPartyGuestPermissions>(r.GetProperty("options")), token); result = null; break;
                case "KickAccount": await KickAccountAsync(userId, sessionId, r.GetProperty("partyId").GetGuid(), r.GetProperty("participantId").GetGuid(), token); result = null; break;
                case "KickGuestAndRevokeInvite": await KickGuestAndRevokeInviteAsync(userId, sessionId, r.GetProperty("partyId").GetGuid(), r.GetProperty("participantId").GetGuid(), token); result = null; break;
                case "UnblockAccount": await UnblockAccountAsync(userId, sessionId, r.GetProperty("partyId").GetGuid(), r.GetProperty("participantId").GetGuid(), token); result = null; break;
                default: throw new WatchPartyException(400, "Unknown watch-party operation.");
            }
            return JsonSerializer.Serialize(new { statusCode = 200, value = result }, Json);
        }
        catch (WatchPartyException ex) { return JsonSerializer.Serialize(new { statusCode = ex.StatusCode, error = ex.Message }, Json); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or ArgumentException)
        { return JsonSerializer.Serialize(new { statusCode = 400, error = "Invalid watch-party request." }, Json); }
        catch (InvalidOperationException)
        { return JsonSerializer.Serialize(new { statusCode = 503, error = "Watch-party provider is not ready." }, Json); }
    }
}
