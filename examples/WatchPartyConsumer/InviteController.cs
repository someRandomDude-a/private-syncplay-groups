using Jellyfin.Plugin.PrivateSyncPlay.Api;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WatchPartyConsumer;

[ApiController, Route("WatchPartyExample"), Authorize(Policy = Policies.SyncPlayHasAccess)]
public sealed class InviteController(IWatchPartyService parties, ISessionManager sessions) : ControllerBase
{
    private (Guid User, string Session) Actor()
    {
        // Never accept a user/session identity in an untrusted request body.
        if (!Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var userId)) throw new WatchPartyException(403, "A user is required.");
        var device = User.FindFirst("Jellyfin-DeviceId")?.Value;
        var client = User.FindFirst("Jellyfin-Client")?.Value;
        var session = sessions.Sessions.FirstOrDefault(s => s.UserId == userId && s.DeviceId == device && s.Client == client)
            ?? throw new WatchPartyException(403, "Use an existing signed-in playback session.");
        return (userId, session.Id);
    }
    [HttpPost("Invite")]
    public async Task<IActionResult> Invite(GenerateLinkRequest request)
    {
        try
        {
            var actor = Actor(); var token = HttpContext.RequestAborted;
            var current = await parties.GetCurrentPartyAsync(actor.User, actor.Session, token);
            var options = new CreateInviteOptions(request.ServerBaseUrl, request.AllowGuests, 2);
            WatchPartyInvite invite;
            if (current is null)
                invite = (await parties.CreatePartyWithInviteAsync(actor.User, actor.Session,
                    new CreatePartyOptions("Plugin-generated party", WatchPartyVisibility.Private, 3), options, token)).Invite;
            else invite = await parties.CreateInviteAsync(actor.User, actor.Session, current.PartyId, options, token);
            return Ok(new { ProviderAvailable = parties.IsAvailable, ClientType = parties.GetType().Name, Invite = invite });
        }
        catch (WatchPartyException ex) { return StatusCode(ex.StatusCode, new { Error = ex.Message }); }
    }
    [HttpDelete("Invites/{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        try
        {
            var actor = Actor(); var token = HttpContext.RequestAborted;
            var party = await parties.GetCurrentPartyAsync(actor.User, actor.Session, token)
                ?? throw new WatchPartyException(404, "Party not found.");
            await parties.RevokeInviteAsync(actor.User, actor.Session, party.PartyId, id, token);
            return NoContent();
        }
        catch (WatchPartyException ex) { return StatusCode(ex.StatusCode, new { Error = ex.Message }); }
    }
    // Convenience wrapper for this example's current-party routes. Identity never comes from a body or route.
    private async Task<IActionResult> Manage(Func<Guid, string, Guid, CancellationToken, Task<IActionResult>> operation)
    {
        try
        {
            var actor = Actor(); var token = HttpContext.RequestAborted;
            var party = await parties.GetCurrentPartyAsync(actor.User, actor.Session, token)
                ?? throw new WatchPartyException(404, "Join a party first.");
            return await operation(actor.User, actor.Session, party.PartyId, token);
        }
        catch (WatchPartyException ex) { return StatusCode(ex.StatusCode, new { Error = ex.Message }); }
    }
    [HttpGet("Management")]
    public Task<IActionResult> Management() => Manage(async (user, session, party, token) => Ok(new
    {
        ProviderAvailable = parties.IsAvailable, ClientType = parties.GetType().Name,
        Management = await parties.GetManagementAsync(user, session, party, token)
    }));
    [HttpPost("Policy")]
    public Task<IActionResult> Policy(WatchPartyPolicy policy) => Manage(async (user, session, party, token) =>
    { await parties.UpdatePolicyAsync(user, session, party, policy, token); return NoContent(); });
    [HttpPost("Accounts/{participant:guid}/Permissions")]
    public Task<IActionResult> AccountPermissions(Guid participant, WatchPartyAccountPermissions permissions) => Manage(async (user, session, party, token) =>
    { await parties.SetAccountPermissionsAsync(user, session, party, participant, permissions, token); return NoContent(); });
    [HttpDelete("Accounts/{participant:guid}")]
    public Task<IActionResult> KickAccount(Guid participant) => Manage(async (user, session, party, token) =>
    { await parties.KickAccountAsync(user, session, party, participant, token); return NoContent(); });
    // The calling UI must confirm that ALL guest leases sharing this invitation will be disconnected.
    [HttpDelete("Guests/{participant:guid}/Admission")]
    public Task<IActionResult> RevokeGuestAdmission(Guid participant) => Manage(async (user, session, party, token) =>
    { await parties.KickGuestAndRevokeInviteAsync(user, session, party, participant, token); return NoContent(); });
    [HttpPost("Blocked/{participant:guid}/Unblock")]
    public Task<IActionResult> Unblock(Guid participant) => Manage(async (user, session, party, token) =>
    { await parties.UnblockAccountAsync(user, session, party, participant, token); return NoContent(); });
}

// In a real integration ServerBaseUrl should come from trusted administrator configuration.
public sealed record GenerateLinkRequest(Uri ServerBaseUrl, bool AllowGuests = false);
