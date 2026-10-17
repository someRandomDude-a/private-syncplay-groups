using System.Reflection;
using Jellyfin.Plugin.PrivateSyncPlay.Api;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class GuestPlayerPolicyTests
{
    [Theory]
    [InlineData("pause")]
    [InlineData("seek")]
    [InlineData("download")]
    [InlineData("cast")]
    [InlineData("futureFeature")]
    public void UnknownAndProtectedFeaturesCanNeverBeGranted(string feature)
    {
        var options = new GuestPlayerOptions { Features = [feature] };
        Assert.Throws<ArgumentException>(options.Validate);
        Assert.False(options.Allows(feature));
        Assert.Equal(403, Assert.Throws<PartyException>(() => GuestPlayerPolicy.Require(options, feature)).Status);
    }
    [Fact] public void EmptyAllowlistIsValidAndNeverGrantsOptionalFeatures()
    {
        var options = new GuestPlayerOptions { Features = [], Controls = [] }; options.Validate();
        using var provider = new ServiceCollection().BuildServiceProvider();
        var view = new GuestPlayerRegistry(provider).View(options);
        Assert.Empty(view.Features); Assert.Empty(view.Controls);
        Assert.Throws<PartyException>(() => GuestPlayerPolicy.Require(options, "subtitles"));
        Assert.Throws<PartyException>(() => GuestPlayerPolicy.Require(null, "subtitles"));
    }
    [Fact] public async Task AValidGuestCannotBypassDisabledSubtitlesByCallingTheEndpointDirectly()
    {
        var config = new Configuration { EnableGuestInvites = true, DefaultGuestPermission = true,
            GuestPlayer = new() { Features = [], Controls = [] } };
        var store = new PartyStore(TimeProvider.System);
        var party = store.Create(Guid.NewGuid(), Guid.NewGuid(), "host", GroupMode.Private, 3);
        var token = store.Issue(party, party.Owner, true, 2, null, config).Token;
        var secret = store.RedeemGuest(token, null, config);
        var context = new DefaultHttpContext(); context.Request.Scheme = "https"; context.Request.Host = new("party.example");
        context.Request.Headers.Cookie = "PrivateSyncPlay.Guest=" + secret;
        var controller = new GuestController(store, null!, () => config) { ControllerContext = new() { HttpContext = context } };
        var encoder = new Mock<MediaBrowser.Controller.MediaEncoding.ISubtitleEncoder>();
        using var subtitles = new GuestSubtitles(encoder.Object, TimeProvider.System);
        Assert.Equal(403, (await Assert.ThrowsAsync<PartyException>(() => controller.Subtitle("generation", 2, subtitles))).Status);
        encoder.VerifyNoOtherCalls();
    }
    [Fact] public void OnlyExplicitlySelectedValidContributionsRenderAndDuplicateProvidersFailClosed()
    {
        var services = new ServiceCollection();
        services.AddPrivateSyncPlayGuestControl(new("example.captions", "subtitles", "Captions", "closed_caption"));
        services.AddPrivateSyncPlayGuestControl(new("evil.pause", "pause", "Pause", "pause"));
        services.AddPrivateSyncPlayGuestControl(new("evil.html", "mute", "Bad icon", "<script>"));
        services.AddPrivateSyncPlayGuestControl(new("conflict.mute", "mute", "Mute one", "volume_off"));
        services.AddPrivateSyncPlayGuestControl(new("conflict.mute", "mute", "Mute two", "volume_off"));
        services.AddKeyedSingleton<string>(GuestPlayerUiExtensions.ServiceKey, "{bad JSON");
        using var provider = services.BuildServiceProvider(); var registry = new GuestPlayerRegistry(provider);
        Assert.DoesNotContain(registry.Catalog(), c => c.Id.StartsWith("evil.") || c.Id.StartsWith("conflict."));
        Assert.DoesNotContain(registry.View(new()).Controls, c => c.Id == "example.captions");
        var options = new GuestPlayerOptions { Features = ["subtitles"], Controls = ["unknown.id", "fullscreen", "example.captions"] };
        Assert.Equal("example.captions", Assert.Single(registry.View(options).Controls).Id);
        options.Features = []; Assert.Empty(registry.View(options).Controls);
    }
    [Fact] public void UnknownGuestActionIsBlockedByTheServerResourceFilter()
    {
        var descriptor = new ControllerActionDescriptor { ControllerTypeInfo = typeof(GuestController).GetTypeInfo(), ActionName = "FutureUnsafeAction" };
        var action = new ActionContext(new DefaultHttpContext(), new RouteData(), descriptor);
        var context = new ResourceExecutingContext(action, [], []);
        new PartyErrors(new PluginStatus(), NullLogger<PartyErrors>.Instance).OnResourceExecuting(context);
        Assert.Equal(403, Assert.IsType<ObjectResult>(context.Result).StatusCode);
    }
    [Fact] public void DesignerCatalogRequiresAdminAndThePreviewUsesARestrictedSyntheticMode()
    {
        Assert.Equal(Policies.RequiresElevation, typeof(GuestPlayerController).GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>().Single().Policy);
        var controller = new AssetsController(new NativeWebStyles(Mock.Of<IApplicationPaths>())) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        var preview = Assert.IsType<ContentResult>(controller.Preview());
        Assert.Contains("data-guest-preview=\"true\"", preview.Content); Assert.Contains("assets/guest-preview.js", preview.Content);
        Assert.Contains("connect-src 'none'", controller.Response.Headers.ContentSecurityPolicy.ToString());
        var watch = Assert.IsType<ContentResult>(controller.Watch());
        Assert.Contains("data-guest-preview=\"false\"", watch.Content); Assert.DoesNotContain("assets/guest-preview.js", watch.Content);
        Assert.Contains("frame-ancestors 'none'", controller.Response.Headers.ContentSecurityPolicy.ToString());
    }
}
