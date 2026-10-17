using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.PrivateSyncPlay;

public sealed class Plugin : BasePlugin<Configuration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }
    public static readonly Guid PluginId = new("09514a9e-f7a0-4c5a-91bc-3a46338298ca");
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public override string Name => "Private SyncPlay";
    public override string Description => "Private parties, limited invites and isolated temporary guest viewing.";
    public override Guid Id => PluginId;
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        ((Configuration)configuration).Validate();
        base.UpdateConfiguration(configuration);
    }
    public IEnumerable<PluginPageInfo> GetPages() => [new()
    {
        Name = "privateSyncPlay",
        EmbeddedResourcePath = "Jellyfin.Plugin.PrivateSyncPlay.Web.settings.html"
    }, new()
    {
        Name = "privateSyncPlayPlayer",
        EmbeddedResourcePath = "Jellyfin.Plugin.PrivateSyncPlay.Web.player-settings.html"
    }];
}
