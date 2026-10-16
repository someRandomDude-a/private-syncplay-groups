using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.PrivateSyncPlay;

public sealed class WebInjection : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuation) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            if (!HttpMethods.IsGet(context.Request.Method) || !(path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/web", StringComparison.OrdinalIgnoreCase)))
            { await continuation(); return; }
            context.Request.Headers.Remove("Accept-Encoding");
            foreach (var header in new[] { "Range", "If-Range", "If-None-Match", "If-Modified-Since" }) context.Request.Headers.Remove(header);
            var original = context.Response.Body;
            using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            try { await continuation(); } finally { context.Response.Body = original; }
            if (context.Response.StatusCode != 200 || context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) != true)
            { buffer.Position = 0; await buffer.CopyToAsync(original); return; }
            var html = Encoding.UTF8.GetString(buffer.ToArray());
            // Relative to /web/; works with a configured Jellyfin base URL as well.
            var index = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (index >= 0) html = html.Insert(index, "<script defer src=\"../PrivateSyncPlay/assets/web.js?v=0.1.0\"></script>");
            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentLength = bytes.Length;
            context.Response.Headers.Remove("ETag");
            context.Response.Headers.Remove("Last-Modified");
            context.Response.Headers.Remove("Accept-Ranges");
            context.Response.Headers.CacheControl = "no-store";
            await original.WriteAsync(bytes);
        });
        next(app);
    };
}
