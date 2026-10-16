using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PrivateSyncPlay;

[ApiController, AllowAnonymous, Route("PrivateSyncPlay/Guest")]
public sealed class GuestController(PartyStore store, GuestMedia media, Func<Configuration> configuration) : ControllerBase
{
    private const string CookieName = "PrivateSyncPlay.Guest";
    private Configuration Config => configuration();
    private string Credential => Request.Cookies[CookieName] ?? string.Empty;
    private void Headers()
    {
        if (!Request.IsHttps) throw new PartyException(403, "Guest viewing requires HTTPS.");
        Response.Headers.CacheControl = "no-store, private";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers.XContentTypeOptions = "nosniff";
    }
    private void SameOrigin()
    {
        var origin = Request.Headers.Origin.ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Authority != Request.Host.Value || uri.Scheme != Request.Scheme)
            throw new PartyException(403, "Same-origin requests are required.");
        if (!Request.IsHttps) throw new PartyException(403, "Guest viewing requires HTTPS.");
    }
    [HttpPost("Redeem")]
    public IActionResult Redeem(TokenRequest request)
    {
        SameOrigin();
        Headers();
        lock (store.Gate)
        {
            store.RateLimit("guest-ip:" + HttpContext.Connection.RemoteIpAddress, 30);
            store.RateLimit("guest-global", 300);
            var secret = store.RedeemGuest(request.Token, Credential, Config);
            Response.Cookies.Append(CookieName, secret, new CookieOptions
            { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = Request.PathBase + "/PrivateSyncPlay/Guest", IsEssential = true });
            return NoContent();
        }
    }
    // Only state polling extends a lease; background media downloads cannot keep a seat alive.
    [HttpGet("State")]
    public object State()
    {
        Headers();
        lock (store.Gate) return media.State(store.GuestParty(Credential, Config, heartbeat: true));
    }
    [HttpGet("media/{generation}/{name}")]
    public IActionResult Stream(string generation, string name)
    {
        Headers();
        lock (store.Gate)
        {
            var data = media.Read(store.GuestParty(Credential, Config), generation, name);
            return File(data.Bytes, data.Type);
        }
    }
    [HttpPost("Leave")]
    public IActionResult Leave()
    {
        SameOrigin();
        Headers();
        lock (store.Gate) store.LeaveGuest(Credential);
        return NoContent();
    }
}
