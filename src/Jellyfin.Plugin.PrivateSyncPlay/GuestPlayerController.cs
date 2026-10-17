using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PrivateSyncPlay;

[ApiController, Route("PrivateSyncPlay/PlayerDesigner"), Authorize(Policy = Policies.RequiresElevation)]
public sealed class GuestPlayerController(GuestPlayerRegistry registry) : ControllerBase
{
    [HttpGet("Catalog")]
    public object Catalog() => new { Features = GuestPlayerPolicy.Features, Controls = registry.Catalog() };
}
