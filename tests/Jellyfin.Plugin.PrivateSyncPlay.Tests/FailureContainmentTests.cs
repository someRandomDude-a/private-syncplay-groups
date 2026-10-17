using Jellyfin.Plugin.PrivateSyncPlay.Api;
using System.Reflection;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.Requests;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class FailureContainmentTests
{
    [Fact] public async Task UnsupportedEngineDisablesPluginWithoutReplacingEngineOrStoppingHost()
    {
        var services = new ServiceCollection();
        var engine = Mock.Of<ISyncPlayManager>();
        services.AddSingleton(engine);
        new ServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>(h => h.ApplicationVersion == new Version(12, 2, 0, 0)));
        services.AddLogging();
        using var provider = services.BuildServiceProvider();
        Assert.Same(engine, provider.GetRequiredService<ISyncPlayManager>());
        Assert.False(provider.GetRequiredService<PluginStatus>().IsAvailable);
        var worker = Assert.IsType<PartyMaintenance>(Assert.Single(provider.GetServices<IHostedService>()));
        await worker.StartAsync(default);
        await worker.ExecuteTask!; // An uncaught BackgroundService exception would stop Jellyfin by default.
        var bridge = provider.GetRequiredKeyedService<Func<Guid, string, string, string, CancellationToken, Task<string>>>(WatchPartyClient.ServiceKey);
        Assert.Contains("503", await bridge(Guid.NewGuid(), "session", "GetCurrentParty", "{}", default));
    }
    [Theory]
    [InlineData(10)]
    [InlineData(13)]
    public void UnsupportedServerMajorDisablesPluginWithoutReplacingEngine(int major)
    {
        var services = new ServiceCollection(); var engine = Mock.Of<ISyncPlayManager>();
        services.AddSingleton(engine);
        var host = Mock.Of<IServerApplicationHost>(h => h.ApplicationVersion == new Version(major, 0, 0, 0));
        new ServiceRegistrator().RegisterServices(services, host);
        using var provider = services.BuildServiceProvider();
        Assert.False(provider.GetRequiredService<PluginStatus>().IsAvailable);
        Assert.Contains("12.x only", provider.GetRequiredService<PluginStatus>().Failure);
        Assert.Same(engine, provider.GetRequiredService<ISyncPlayManager>());
    }

    [Fact] public async Task BackgroundDependencyFailureRevokesLeasesAndNeverFaultsHostTask()
    {
        var status = new PluginStatus(); var store = new PartyStore(TimeProvider.System);
        var config = new Configuration { EnableGuestInvites = true, DefaultGuestPermission = true };
        var party = store.Create(Guid.NewGuid(), Guid.NewGuid(), "host", GroupMode.Private, 3);
        var guest = store.RedeemGuest(store.Issue(party, party.Owner, true, 2, null, config).Token, null, config);
        var services = new ServiceCollection(); services.AddSingleton(store);
        services.AddSingleton<PrivateSyncPlayManager>(_ => throw new InvalidOperationException("Simulated dependency failure"));
        using var provider = services.BuildServiceProvider();
        using var worker = new PartyMaintenance(provider, status, NullLogger<PartyMaintenance>.Instance);
        await worker.StartAsync(default); await worker.ExecuteTask!;
        Assert.False(status.IsAvailable); Assert.Empty(store.Parties);
        Assert.Equal(401, Assert.Throws<PartyException>(() => store.GuestParty(guest, config)).Status);
    }

    [Fact] public void DisabledPluginShortCircuitsBeforeControllerActivation()
    {
        var status = new PluginStatus(); status.Disable("Conflict");
        var descriptor = new ControllerActionDescriptor { ControllerTypeInfo = typeof(GuestController).GetTypeInfo() };
        var action = new ActionContext(new DefaultHttpContext(), new RouteData(), descriptor);
        var context = new ResourceExecutingContext(action, [], []);
        new PartyErrors(status, NullLogger<PartyErrors>.Instance).OnResourceExecuting(context);
        Assert.Equal(503, Assert.IsType<ObjectResult>(context.Result).StatusCode);
        Assert.Equal(503, Assert.Throws<PartyException>(status.EnsureAvailable).Status);
    }
    [Fact] public void UnexpectedGuestErrorDoesNotExposeServerDiagnostics()
    {
        var descriptor = new ControllerActionDescriptor { ControllerTypeInfo = typeof(GuestController).GetTypeInfo() };
        var action = new ActionContext(new DefaultHttpContext(), new RouteData(), descriptor);
        var context = new ExceptionContext(action, []) { Exception = new IOException("/private/media/file.mp4 secret-token") };
        new PartyErrors(new PluginStatus(), NullLogger<PartyErrors>.Instance).OnException(context);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(503, result.StatusCode); Assert.True(context.ExceptionHandled);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.DoesNotContain("/private", json); Assert.DoesNotContain("secret-token", json);
    }

    [Fact] public void NativeCleanupFailureCannotKeepGuestCredentialsOrAllowOrphanGroupControls()
    {
        var store = new PartyStore(TimeProvider.System);
        var config = new Configuration { EnableGuestInvites = true, DefaultGuestPermission = true };
        var sessions = new Mock<ISessionManager>();
        var session = new SessionInfo(sessions.Object, NullLogger.Instance) { Id = "host", UserId = Guid.NewGuid() };
        sessions.SetupGet(s => s.Sessions).Returns([session]);
        var engine = new Mock<ISyncPlayManager>();
        engine.Setup(e => e.LeaveGroup(It.IsAny<SessionInfo>(), It.IsAny<LeaveGroupRequest>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("Native engine failed"));
        using var manager = new PrivateSyncPlayManager(new CoreEngine(engine.Object), store, sessions.Object, () => config);
        var party = store.Create(Guid.NewGuid(), session.UserId, session.Id, GroupMode.Private, 3);
        var guest = store.RedeemGuest(store.Issue(party, party.Owner, true, 2, null, config).Token, null, config);
        manager.EndParty(party.Id);
        Assert.Empty(store.Parties);
        Assert.Throws<PartyException>(() => store.GuestParty(guest, config));
        Assert.Throws<PartyException>(() => manager.HandleRequest(session, null!, default));
        engine.Verify(e => e.HandleRequest(It.IsAny<SessionInfo>(), It.IsAny<IGroupPlaybackRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"statusCode\":\"200\"}")]
    [InlineData("{\"statusCode\":200}")]
    [InlineData("{\"statusCode\":700,\"error\":\"bad provider\"}")]
    [InlineData("not json")]
    public async Task MalformedProviderRepliesBecomeSafeUnavailableErrors(string reply)
    {
        var services = new ServiceCollection().AddPrivateSyncPlayClient();
        services.AddKeyedSingleton<Func<Guid, string, string, string, CancellationToken, Task<string>>>(WatchPartyClient.ServiceKey,
            (_, _) => (_, _, _, _, _) => Task.FromResult(reply));
        using var provider = services.BuildServiceProvider();
        var error = await Assert.ThrowsAsync<WatchPartyException>(() => provider.GetRequiredService<IWatchPartyService>().GetCurrentPartyAsync(Guid.NewGuid(), "host"));
        Assert.Equal(503, error.StatusCode);
    }

    [Fact] public void InvalidConfigurationEntriesAreRejectedWithoutNullReferenceFailures()
    {
        Assert.Throws<ArgumentException>(() => new Configuration { Users = [null!] }.Validate());
        Assert.Throws<ArgumentException>(() => new Configuration { MaxGuestStreams = 0 }.Validate());
    }
}
