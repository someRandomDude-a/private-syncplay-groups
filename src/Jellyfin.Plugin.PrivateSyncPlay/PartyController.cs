using System.ComponentModel.DataAnnotations;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay.Requests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Jellyfin.Database.Implementations.Enums;

namespace Jellyfin.Plugin.PrivateSyncPlay;

[ApiController, Route("PrivateSyncPlay"), Authorize(Policy = Policies.SyncPlayHasAccess), RequestSizeLimit(8192)]
public sealed class PartyController(PrivateSyncPlayManager manager, PartyStore store, ISessionManager sessions, IUserManager users) : ControllerBase
{
    private async Task<SessionInfo> Session()
    {
        string Claim(string name) => User.FindFirst("Jellyfin-" + name)?.Value ?? string.Empty;
        if (!Guid.TryParse(Claim("UserId"), out var id) || id == Guid.Empty || string.IsNullOrEmpty(Claim("DeviceId")))
            throw new PartyException(403, "A signed-in user and device are required.");
        var user = users.GetUserById(id) ?? throw new PartyException(403, "User not found.");
        return await sessions.LogSessionActivity(Claim("Client"), Claim("Version"), Claim("DeviceId"), Claim("Device"),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty, user);
    }

    [HttpGet("Me")]
    public async Task<object> Me()
    {
        var session = await Session();
        lock (store.Gate)
        {
            store.Sweep(manager.Config);
            var party = store.ForSession(session.Id);
            var canCreate = manager.Config.CanCreate(session.UserId, GroupMode.Public) || manager.Config.CanCreate(session.UserId, GroupMode.Private);
            return new { RealtimeReady = session.SessionControllers.Any(c => c.IsSessionActive),
                DefaultMode = canCreate ? manager.Config.DefaultFor(session.UserId).ToString() : null,
                CanCreatePublic = manager.Config.CanCreate(session.UserId, GroupMode.Public),
                CanCreatePrivate = manager.Config.CanCreate(session.UserId, GroupMode.Private),
                CanInviteGuests = manager.Config.CanInviteGuests(session.UserId), manager.Config.MaxViewers, manager.Config.MaxInviteUses,
                Party = party is null ? null : new { party.Id, Mode = party.Mode.ToString(), party.Capacity,
                    IsOwner = party.Owner == session.UserId, AllowGuests = party.Policy.AllowGuests, AcceptingJoins = party.Policy.AllowJoins,
                    CanControlPlayback = party.CanControl(session.UserId), CanEditQueue = party.CanEditQueue(session.UserId),
                    Code = party.Owner == session.UserId && party.Mode == GroupMode.Private ? party.Code : null,
                    Viewers = party.Members.Count + store.GuestCount(party.Id),
                    Invites = party.Owner == session.UserId ? store.Invites(party.Id).ToArray() : [] } };
        }
    }
    [HttpPost("Groups"), Authorize(Policy = Policies.SyncPlayCreateGroup)]
    public async Task<object> Create(CreateRequest request)
    {
        var session = await Session();
        return manager.Create(session, new NewGroupRequest(request.Name.Trim()), request.Mode, request.MaxViewers, HttpContext.RequestAborted);
    }
    [HttpPost("JoinCode"), Authorize(Policy = Policies.SyncPlayJoinGroup)]
    public async Task<IActionResult> JoinCode(CodeRequest request)
    {
        manager.JoinCode(await Session(), request.Code, HttpContext.RequestAborted);
        return NoContent();
    }
    [HttpPost("JoinInvite"), Authorize(Policy = Policies.SyncPlayJoinGroup)]
    public async Task<IActionResult> JoinInvite(TokenRequest request)
    {
        manager.JoinInvite(await Session(), request.Token, HttpContext.RequestAborted);
        return NoContent();
    }
    [HttpPost("Invites")]
    public async Task<object> Invite(InviteRequest request)
    {
        var session = await Session();
        lock (store.Gate)
        {
            var party = store.ForSession(session.Id) ?? throw new PartyException(404, "Join a party first.");
            var invite = store.Issue(party, session.UserId, request.AllowGuests, request.MaxUses, request.ExpiresAt, manager.Config);
            return new { invite.Value.Id, invite.Token };
        }
    }
    [HttpDelete("Invites/{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        var session = await Session();
        lock (store.Gate)
        {
            var party = store.ForSession(session.Id) ?? throw new PartyException(404, "Party not found.");
            if (party.Owner != session.UserId) throw new PartyException(403, "Only the owner may revoke invitations.");
            store.Revoke(party, id);
        }
        return NoContent();
    }
    [HttpGet("Groups/{id:guid}/Management")]
    public async Task<object> Management(Guid id)
    {
        var actor = await Session();
        lock (store.Gate)
        {
            store.Sweep(manager.Config); var party = manager.OwnedParty(actor, id);
            var snapshot = PartyManagementSnapshot.Capture(party, store, sessions, users, manager.Config);
            var participants = snapshot.Accounts.Select(a => (object)new { Id = a.ParticipantId, a.Name, Kind = "account", Sessions = a.SessionCount, a.IsOwner,
                CanControlPlayback = a.EffectivePermissions.ControlPlayback, CanEditQueue = a.EffectivePermissions.EditQueue,
                PlaybackAllowed = a.Permissions.ControlPlayback, QueueAllowed = a.Permissions.EditQueue }).ToList();
            participants.AddRange(snapshot.Guests.Select(g => (object)new { Id = g.ParticipantId, g.Name, Kind = "guest", Sessions = 1, IsOwner = false,
                GuestFeatures = g.EffectiveFeatures, InheritsGuestFeatures = g.Permissions.Features is null, SharedInviteViewers = g.SharedInviteGuestCount }));
            return new { party.Id, party.Capacity, Policy = party.Policy with { GuestFeatures = snapshot.EffectiveGuestFeatures.ToArray() },
                AllowedGuestFeatures = snapshot.AllowedGuestFeatures, Participants = participants,
                Blocked = snapshot.BlockedAccounts.Select(a => new { Id = a.ParticipantId, a.Name }).ToArray() };
        }
    }
    [HttpPost("Groups/{id:guid}/Policy")]
    public async Task<IActionResult> Policy(Guid id, PartyPolicyRequest request)
    {
        manager.UpdatePolicy(await Session(), id, new(request.AllowJoins!.Value, request.AllowGuests!.Value,
            request.ParticipantsControlPlayback!.Value, request.ParticipantsEditQueue!.Value, request.GuestFeatures), request.MaxViewers!.Value);
        return NoContent();
    }
    [HttpPost("Groups/{id:guid}/Participants/{participant:guid}/Permissions")]
    public async Task<IActionResult> Permissions(Guid id, Guid participant, ParticipantPermissionRequest request)
    {
        var actor = await Session();
        if (request.Kind == "account")
        {
            if (!request.ControlPlayback.HasValue || !request.EditQueue.HasValue) throw new PartyException(400, "Both account permissions are required.");
            manager.SetAccountPermissions(actor, id, participant, request.ControlPlayback.Value, request.EditQueue.Value);
        }
        else if (request.Kind == "guest") manager.SetGuestPermissions(actor, id, participant, request.GuestFeatures);
        else throw new PartyException(400, "Unknown participant kind.");
        return NoContent();
    }
    [HttpDelete("Groups/{id:guid}/Participants/{participant:guid}")]
    public async Task<IActionResult> Kick(Guid id, Guid participant)
    { manager.Kick(await Session(), id, participant); return NoContent(); }
    [HttpPost("Groups/{id:guid}/Blocked/{participant:guid}/Unblock")]
    public async Task<IActionResult> Unblock(Guid id, Guid participant)
    { manager.Unblock(await Session(), id, participant); return NoContent(); }
    [HttpDelete("Groups/{id:guid}")]
    public async Task<IActionResult> EndParty(Guid id)
    {
        var actor = await Session();
        lock (store.Gate)
        {
            var party = store.Require(id);
            if (party.Owner != actor.UserId && users.GetUserById(actor.UserId)?.Permissions.Any(p => p.Kind == PermissionKind.IsAdministrator && p.Value) != true)
                throw new PartyException(403, "Only the owner or administrator may end this party.");
            manager.EndParty(id);
        }
        return NoContent();
    }
}

public sealed record CreateRequest([Required, StringLength(200, MinimumLength = 1)] string Name, GroupMode? Mode, int? MaxViewers);
public sealed record CodeRequest([Required, StringLength(8, MinimumLength = 6)] string Code);
public sealed record TokenRequest([Required, StringLength(64, MinimumLength = 64)] string Token);
public sealed record InviteRequest(bool AllowGuests, int MaxUses, DateTimeOffset? ExpiresAt);
public sealed record PartyPolicyRequest([Required] bool? AllowJoins, [Required] bool? AllowGuests,
    [Required] bool? ParticipantsControlPlayback, [Required] bool? ParticipantsEditQueue, [Required, Range(1, 1000)] int? MaxViewers,
    [MaxLength(6)] string[]? GuestFeatures);
public sealed record ParticipantPermissionRequest([Required] string Kind, bool? ControlPlayback, bool? EditQueue, [MaxLength(6)] string[]? GuestFeatures);
