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
        TargetAbi = ServerCompatibility.TargetAbi,
        SupportedServerVersion = ServerCompatibility.TargetAbi,
        ApiVersion = typeof(IWatchPartyService).Assembly.GetName().Version?.ToString()
    };
}
