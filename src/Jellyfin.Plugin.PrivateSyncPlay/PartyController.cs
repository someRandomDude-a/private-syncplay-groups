using System.ComponentModel.DataAnnotations;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay.Requests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PrivateSyncPlay;

[ApiController, Route("PrivateSyncPlay"), Authorize(Policy = Policies.SyncPlayHasAccess)]
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
                    IsOwner = party.Owner == session.UserId, Code = party.Owner == session.UserId && party.Mode == GroupMode.Private ? party.Code : null,
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
    [HttpDelete("Groups/{id:guid}"), Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult EndParty(Guid id) { manager.EndParty(id); return NoContent(); }
}

public sealed record CreateRequest([Required, StringLength(200, MinimumLength = 1)] string Name, GroupMode? Mode, int? MaxViewers);
public sealed record CodeRequest([Required, StringLength(8, MinimumLength = 6)] string Code);
public sealed record TokenRequest([Required, StringLength(64, MinimumLength = 64)] string Token);
public sealed record InviteRequest(bool AllowGuests, int MaxUses, DateTimeOffset? ExpiresAt);
