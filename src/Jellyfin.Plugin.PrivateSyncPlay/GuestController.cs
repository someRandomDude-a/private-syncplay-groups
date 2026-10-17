using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PrivateSyncPlay;

[ApiController, AllowAnonymous, Route("PrivateSyncPlay/Guest"), RequestSizeLimit(4096)]
public sealed class GuestController(PartyStore store, GuestMedia media, Func<Configuration> configuration) : ControllerBase
{
    private const string CookieName = "PrivateSyncPlay.Guest";
    private Configuration Config => configuration();
    private string Credential => Request.Cookies[CookieName] ?? string.Empty;
    private void Headers()
    {
        if (!Request.IsHttps) throw new PartyException(403, "Guest viewing requires HTTPS.");
        var site = Request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && site is not ("same-origin" or "none")) throw new PartyException(403, "Same-origin requests are required.");
        // SameSite cookies are site-scoped, not origin-scoped: also reject sibling-subdomain requests.
        if (Request.Headers.ContainsKey("Origin")) SameOrigin();
        Response.Headers.CacheControl = "no-store, private";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers.XContentTypeOptions = "nosniff";
    }
    private void SameOrigin()
    {
        var origin = Request.Headers.Origin.ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || !Uri.TryCreate(Request.Scheme + "://" + Request.Host.Value, UriKind.Absolute, out var expected)
            || !string.Equals(uri.Host, expected.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != expected.Port
            || uri.Scheme != expected.Scheme || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
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
        lock (store.Gate)
        {
            var party = store.GuestParty(Credential, Config);
            store.RateLimitGuest(Credential, "state", 120);
            store.GuestParty(Credential, Config, heartbeat: true);
            return media.State(party);
        }
    }
    [HttpGet("media/{generation}/{name}")]
    public IActionResult Stream(string generation, string name)
    {
        Headers();
        lock (store.Gate)
        {
            var party = store.GuestParty(Credential, Config);
            store.RateLimitGuest(Credential, "media", 600);
            var data = media.Read(party, generation, name);
            return File(data.Bytes, data.Type);
        }
    }
    [HttpPost("Leave")]
    public IActionResult Leave()
    {
        SameOrigin();
        Headers();
        lock (store.Gate) store.LeaveGuest(Credential);
        Response.Cookies.Delete(CookieName, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict,
            Path = Request.PathBase + "/PrivateSyncPlay/Guest" });
        return NoContent();
    }
}
