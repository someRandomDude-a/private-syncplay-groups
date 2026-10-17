using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class WebInjectionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LauncherUsesRunningAssemblyVersionAndIsSkippedWhenDisabled(bool enabled)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var builder = new ApplicationBuilder(services);
        var status = new PluginStatus();
        if (!enabled) status.Disable("Test failure");
        new WebInjection(status).Configure(app => app.Run(async context =>
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html";
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("<html><body>Jellyfin</body></html>"));
        }))(builder);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/web/index.html";
        using var output = new MemoryStream(); context.Response.Body = output;
        await builder.Build()(context);
        var html = Encoding.UTF8.GetString(output.ToArray());
        if (enabled)
            Assert.Contains("web.js?v=" + typeof(Plugin).Assembly.GetName().Version, html);
        else Assert.DoesNotContain("PrivateSyncPlay/assets/web.js", html);
    }
}
