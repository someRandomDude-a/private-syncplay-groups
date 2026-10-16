using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.SyncPlay;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.PrivateSyncPlay;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        var core = services.LastOrDefault(x => x.ServiceType == typeof(ISyncPlayManager))
            ?? throw new InvalidOperationException("No SyncPlay engine registered.");
        if (core.ImplementationType?.FullName != "Emby.Server.Implementations.SyncPlay.SyncPlayManager")
            throw new InvalidOperationException("Private SyncPlay cannot safely wrap another replacement SyncPlay plugin.");
        services.Remove(core);
        services.AddSingleton(sp => new CoreEngine((ISyncPlayManager)ActivatorUtilities.CreateInstance(sp, core.ImplementationType)));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<Func<Configuration>>(_ => () => Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin is not loaded."));
        services.AddSingleton<PartyStore>();
        services.AddSingleton<PrivateSyncPlayManager>();
        services.AddSingleton<ISyncPlayManager>(sp => sp.GetRequiredService<PrivateSyncPlayManager>());
        services.AddSingleton<GuestMedia>();
        services.AddSingleton<GuestEncoding>();
        services.AddSingleton<IStartupFilter, WebInjection>();
        services.Configure<MvcOptions>(o => o.Filters.Add<PartyErrors>());
        services.AddHostedService<PartyMaintenance>();
    }
}

public sealed class PartyErrors : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not PartyException error) return;
        context.Result = new ObjectResult(new { Error = error.Message }) { StatusCode = error.Status };
        context.ExceptionHandled = true;
    }
}

public sealed class PartyMaintenance(ISyncPlayManager manager, PrivateSyncPlayManager expected, PartyStore store, GuestMedia media,
    MediaBrowser.Controller.Session.ISessionManager sessions, MediaBrowser.Controller.Library.IUserManager users) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!ReferenceEquals(manager, expected)) throw new InvalidOperationException("SyncPlay privacy enforcement was replaced by another plugin.");
        // Subscribe before serving requests so a deny->allow toggle cannot resurrect a revoked lease.
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin is not loaded.");
        void Changed(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration config)
        { lock (store.Gate) { store.Sweep((Configuration)config); media.Reap(); } }
        plugin.ConfigurationChanged += Changed;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                lock (store.Gate)
                {
                    var active = sessions.Sessions.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var session in store.Parties.Values.SelectMany(x => x.Members).Distinct().Where(x => !active.Contains(x)).ToArray())
                        store.RemoveSession(session);
                    store.Sweep(expected.Config);
                    foreach (var party in store.Parties.Values.ToArray())
                    {
                        var owner = users.GetUserById(party.Owner);
                        if (owner is null || owner.Permissions.Any(p => p.Kind == Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled && p.Value))
                            store.Close(party.Id);
                    }
                    media.Reap();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { plugin.ConfigurationChanged -= Changed; media.Dispose(); }
    }
}
