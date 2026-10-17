using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PrivateSyncPlay;

[ApiController, AllowAnonymous, Route("PrivateSyncPlay")]
public sealed class AssetsController(NativeWebStyles styles) : ControllerBase
{
    [HttpGet("Watch")]
    public IActionResult Watch()
    {
        // HTTPS styles/fonts allow administrator-installed @import skins, not external scripts or APIs.
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self' https: 'unsafe-inline'; font-src 'self' data: https:; connect-src 'self'; media-src 'self' blob:; worker-src 'self' blob:; img-src 'self' data: https:; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers.CacheControl = "no-store";
        using var input = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.PrivateSyncPlay.Web.guest.html")!;
        using var reader = new StreamReader(input);
        return Content(reader.ReadToEnd().Replace("<!--JELLYFIN_THEME-->", styles.ThemeLink)
            .Replace("{{THEME}}", System.Net.WebUtility.HtmlEncode(styles.Theme)), "text/html; charset=utf-8");
    }
    [HttpGet("assets/{name}")]
    public IActionResult Asset(string name) => name switch
    {
        "web.js" or "guest.js" or "ui.js" => Resource(name, "text/javascript; charset=utf-8"),
        "ui.css" => Resource(name, "text/css; charset=utf-8"),
        "native.css" => Content(styles.Css, "text/css; charset=utf-8"),
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
