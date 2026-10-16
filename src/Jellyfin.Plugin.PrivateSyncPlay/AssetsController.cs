using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PrivateSyncPlay;

[ApiController, AllowAnonymous, Route("PrivateSyncPlay")]
public sealed class AssetsController : ControllerBase
{
    [HttpGet("Watch")]
    public IActionResult Watch()
    {
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; media-src 'self' blob:; worker-src 'self' blob:; img-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers.CacheControl = "no-store";
        return Resource("guest.html", "text/html; charset=utf-8");
    }
    [HttpGet("assets/{name}")]
    public IActionResult Asset(string name) => name switch
    {
        "web.js" or "guest.js" => Resource(name, "text/javascript; charset=utf-8"),
        "hls.js" => Resource("hls.js", "text/javascript; charset=utf-8", true),
        "hls.LICENSE" => Resource("hls.LICENSE", "text/plain; charset=utf-8", true),
        _ => NotFound()
    };
    private IActionResult Resource(string name, string type, bool vendor = false)
    {
        Response.Headers.XContentTypeOptions = "nosniff";
        var key = vendor ? "PrivateSyncPlay." + name : "Jellyfin.Plugin.PrivateSyncPlay.Web." + name;
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(key);
        return stream is null ? NotFound() : File(stream, type);
    }
}
