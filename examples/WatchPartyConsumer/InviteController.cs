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
}

// In a real integration ServerBaseUrl should come from trusted administrator configuration.
public sealed record GenerateLinkRequest(Uri ServerBaseUrl, bool AllowGuests = false);
