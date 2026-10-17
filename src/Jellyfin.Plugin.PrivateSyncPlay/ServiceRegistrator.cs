using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.SyncPlay;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Jellyfin.Plugin.PrivateSyncPlay.Api;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PrivateSyncPlay;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        var status = new PluginStatus();
        services.AddSingleton(status);
        services.Configure<MvcOptions>(o => o.Filters.Add<PartyErrors>());
        services.AddKeyedSingleton<Func<Guid, string, string, string, CancellationToken, Task<string>>>(WatchPartyClient.ServiceKey,
            (sp, _) => (user, session, operation, json, token) => status.IsAvailable
                ? sp.GetRequiredService<WatchPartyService>().InvokeAsync(user, session, operation, json, token)
                : Task.FromResult("{\"statusCode\":503,\"error\":\"Private SyncPlay is unavailable.\"}"));
        if (applicationHost.ApplicationVersion is not { Major: 12 })
        {
            status.Disable("Private SyncPlay supports Jellyfin 12.x only. The existing server engine was not replaced.");
            services.AddHostedService<PartyMaintenance>();
            return;
        }
        var core = services.LastOrDefault(x => x.ServiceType == typeof(ISyncPlayManager));
        if (core?.ImplementationType?.FullName != "Emby.Server.Implementations.SyncPlay.SyncPlayManager"
            || core.Lifetime != ServiceLifetime.Singleton)
        {
            status.Disable("Unsupported or conflicting SyncPlay registration. Private SyncPlay is disabled; the existing engine was not replaced.");
            services.AddHostedService<PartyMaintenance>();
            return;
        }
        services.Remove(core);
        services.AddSingleton(sp => new CoreEngine((ISyncPlayManager)ActivatorUtilities.CreateInstance(sp, core.ImplementationType)));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<Func<Configuration>>(_ => () => Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin is not loaded."));
        services.AddSingleton<PartyStore>();
        services.AddSingleton<PrivateSyncPlayManager>();
        services.AddSingleton<ISyncPlayManager>(sp => sp.GetRequiredService<PrivateSyncPlayManager>());
        services.AddSingleton<GuestMedia>();
        services.AddSingleton<GuestEncoding>();
        services.AddSingleton<NativeWebStyles>();
        services.AddSingleton<WatchPartyService>();
        services.AddSingleton<IWatchPartyService>(sp => sp.GetRequiredService<WatchPartyService>());
        services.AddSingleton<IStartupFilter, WebInjection>();
        services.AddHostedService<PartyMaintenance>();
    }
}

public sealed class PartyErrors(PluginStatus status, ILogger<PartyErrors> logger) : IExceptionFilter, IResourceFilter
{
    private static bool IsPlugin(Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor action)
        => action is ControllerActionDescriptor controller && controller.ControllerTypeInfo.Assembly == typeof(Plugin).Assembly;
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (IsPlugin(context.ActionDescriptor))
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store, private";
            context.HttpContext.Response.Headers.XContentTypeOptions = "nosniff";
        }
        // Runs before controller activation, including when incompatible dependencies are deliberately not registered.
        if (IsPlugin(context.ActionDescriptor) && !status.IsAvailable)
            context.Result = new ObjectResult(new { Error = "Private SyncPlay is unavailable." }) { StatusCode = 503 };
    }
    public void OnResourceExecuted(ResourceExecutedContext context) { }
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is PartyException error)
            context.Result = new ObjectResult(new { Error = error.Message }) { StatusCode = error.Status };
        else if (IsPlugin(context.ActionDescriptor))
        {
            // Do not return paths, encoder arguments, invitation tokens or provider diagnostics to anonymous callers.
            logger.LogError("Private SyncPlay request failed ({ErrorType}).", context.Exception.GetType().Name);
            context.Result = new ObjectResult(new { Error = "Private SyncPlay could not complete this request." }) { StatusCode = 503 };
        }
        else return;
        context.ExceptionHandled = true;
    }
}

public sealed class PartyMaintenance(IServiceProvider services, PluginStatus status, ILogger<PartyMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        PartyStore? store = null;
        GuestMedia? media = null;
        Plugin? plugin = null;
        void Cleanup()
        {
            try { media?.Dispose(); }
            catch (Exception error) { logger.LogError("Guest media cleanup failed ({ErrorType}); access remains disabled.", error.GetType().Name); }
        }
        void Fail(Exception? error = null)
        {
            status.Disable("Private SyncPlay safety checks failed. Restart after correcting the configuration or plugin conflict.");
            logger.LogError("{Failure} ({ErrorType})", status.Failure, error?.GetType().Name ?? "CompatibilityCheck");
            // Revoke access FIRST. Cleanup failures cannot preserve a usable guest credential.
            if (store is not null) lock (store.Gate) foreach (var id in store.Parties.Keys.ToArray()) store.Close(id);
            Cleanup();
        }
        void Changed(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration config)
        {
            try { lock (store!.Gate) { store.Sweep((Configuration)config); media!.Reap(); } }
            catch (Exception error) { Fail(error); }
        }
        try
        {
            if (!status.IsAvailable) { Fail(); return; }
            store = services.GetRequiredService<PartyStore>();
            var expected = services.GetRequiredService<PrivateSyncPlayManager>();
            if (!ReferenceEquals(services.GetRequiredService<ISyncPlayManager>(), expected)) { Fail(); return; }
            var sessions = services.GetRequiredService<MediaBrowser.Controller.Session.ISessionManager>();
            var users = services.GetRequiredService<MediaBrowser.Controller.Library.IUserManager>();
            media = services.GetRequiredService<GuestMedia>();
            plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin is not loaded.");
            expected.Config.Validate();
            plugin.ConfigurationChanged += Changed;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!status.IsAvailable) return;
                lock (store.Gate)
                {
                    var members = store.Parties.Values.SelectMany(x => x.Members).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var active = sessions.Sessions.Where(x => members.Contains(x.Id) && x.UserId != Guid.Empty).Where(x =>
                    {
                        var user = users.GetUserById(x.UserId);
                        return user is not null && user.SyncPlayAccess != Jellyfin.Database.Implementations.Enums.SyncPlayUserAccessType.None
                            && !user.Permissions.Any(p => p.Kind == Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled && p.Value);
                    }).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var session in members.Where(x => !active.Contains(x)))
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
        catch (Exception error) { Fail(error); }
        finally { if (plugin is not null) plugin.ConfigurationChanged -= Changed; Cleanup(); }
    }
}
