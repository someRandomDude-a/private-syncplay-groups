using Jellyfin.Plugin.PrivateSyncPlay.Api;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PrivateSyncPlay;

[ApiController, Route("PrivateSyncPlay/Version"), Authorize(Policy = Policies.RequiresElevation)]
public sealed class VersionController(IServerApplicationHost host) : ControllerBase
{
    [HttpGet]
    public object Get() => new
    {
        PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString(),
        ServerVersion = host.ApplicationVersion.ToString(),
        TargetAbi = "12.0.0.0",
        SupportedServerMajor = 12,
        ApiVersion = typeof(IWatchPartyService).Assembly.GetName().Version?.ToString()
    };
}
