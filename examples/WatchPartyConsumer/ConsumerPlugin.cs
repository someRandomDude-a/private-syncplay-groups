using Jellyfin.Plugin.PrivateSyncPlay.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace WatchPartyConsumer;

// Example/test plugin only. It is NOT included in the production Private SyncPlay package.
public sealed class ConsumerPlugin(IApplicationPaths paths, IXmlSerializer serializer) : BasePlugin<BasePluginConfiguration>(paths, serializer)
{
    public override string Name => "Watch Party API Example";
    public override Guid Id => new("d9bcd474-9ee1-4bfe-bf01-617e6a3465a9");
}
public sealed class Registrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
        => services.AddPrivateSyncPlayClient().AddPrivateSyncPlayGuestControl(
            new("example.captions", "subtitles", "Example plugin captions", "closed_caption")).AddSingleton<PartyAutomation>();
}
