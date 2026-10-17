using System.Reflection;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class GuestSecurityTests
{
    private static GuestController Controller(string scheme = "https", string origin = "https://party.example")
    {
        var store = new PartyStore(TimeProvider.System);
        var paths = new Mock<IApplicationPaths>(); paths.SetupGet(x => x.CachePath).Returns(Path.GetTempPath());
        var media = new GuestMedia(store, Mock.Of<ISessionManager>(), Mock.Of<ILibraryManager>(),
            Mock.Of<IMediaEncoder>(), paths.Object, Mock.Of<IUserManager>(),
            new GuestEncoding(null!, Mock.Of<IConfigurationManager>(), Mock.Of<IMediaSourceManager>()));
        var http = new DefaultHttpContext();
        http.Request.Scheme = scheme;
        http.Request.Host = new HostString("party.example");
        http.Request.Headers.Origin = origin;
        return new GuestController(store, media, () => new Configuration()) { ControllerContext = new ControllerContext { HttpContext = http } };
    }
    [Theory]
    [InlineData("http", "http://party.example")]
    [InlineData("https", "https://evil.example")]
    [InlineData("https", "null")]
    [InlineData("https", "")]
    public void GuestRedemptionRequiresHttpsAndSameOrigin(string scheme, string origin)
    {
        Assert.Equal(403, Assert.Throws<PartyException>(() => Controller(scheme, origin).Redeem(new TokenRequest(new string('A', 64)))).Status);
    }
    [Fact] public void UnknownGuestCannotAccessStateOrSegments()
    {
        Assert.Equal(401, Assert.Throws<PartyException>(() => Controller().State()).Status);
        Assert.Equal(401, Assert.Throws<PartyException>(() => Controller().Stream("anything", "index.m3u8")).Status);
    }
    [Fact] public void SiblingSubdomainCannotReadGuestStateEvenWithACookie()
    {
        Assert.Equal(403, Assert.Throws<PartyException>(() => Controller(origin: "https://other.party.example").State()).Status);
        var c = Controller(); c.Request.Headers.Remove("Origin"); c.Request.Headers["Sec-Fetch-Site"] = "same-site";
        Assert.Equal(403, Assert.Throws<PartyException>(() => c.Stream("anything", "index.m3u8")).Status);
    }
    [Fact] public void HostTokensAreNotAcceptedAsGuestCredentials()
    {
        var c = Controller();
        c.Request.Headers.Authorization = "MediaBrowser Token=\"pretend-host-token\"";
        Assert.Equal(401, Assert.Throws<PartyException>(() => c.State()).Status);
    }
    [Fact] public void GuestControllerExposesNoControlOrLibraryRoutes()
    {
        var methods = typeof(GuestController).GetMethods(BindingFlags.Public | BindingFlags.DeclaredOnly | BindingFlags.Instance);
        Assert.Equal(new[] { "Leave", "Redeem", "State", "Stream" }, methods.Select(m => m.Name).Order().ToArray());
        Assert.NotNull(typeof(GuestController).GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.DoesNotContain(typeof(ServiceRegistrator).Assembly.GetTypes(), t => t.GetInterfaces().Any(i => i.FullName == "Microsoft.AspNetCore.Authentication.IAuthenticationHandler"));
    }
    [Fact] public void SignedInRoutesRequireJellyfinPolicies()
    {
        Assert.Equal(Policies.SyncPlayHasAccess, typeof(PartyController).GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal(Policies.SyncPlayCreateGroup, typeof(PartyController).GetMethod("Create")!.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal(Policies.SyncPlayJoinGroup, typeof(PartyController).GetMethod("JoinCode")!.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal(Policies.RequiresElevation, typeof(PartyController).GetMethod("EndParty")!.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }
    [Fact] public void AssetsUseAnExplicitAllowlist()
    {
        var c = new AssetsController(new NativeWebStyles(Mock.Of<IApplicationPaths>())) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<NotFoundResult>(c.Asset("../../Plugin.cs"));
        Assert.IsType<NotFoundResult>(c.Asset("settings.html"));
        Assert.IsType<FileStreamResult>(c.Asset("hls.js"));
    }
}
