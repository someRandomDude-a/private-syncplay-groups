using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PrivateSyncPlay.Tests;

public sealed class GuestJoinNotificationTests
{
    [Fact] public void GuestLabelsAreStableSequentialAndPartyScopedAndRefreshDoesNotAnnounceAgain()
    {
        var store = new PartyStore(TimeProvider.System);
        var config = new Configuration { EnableGuestInvites = true, DefaultGuestPermission = true };
        var sessions = new Mock<ISessionManager>();
        var owner = Guid.NewGuid(); var friend = Guid.NewGuid();
        SessionInfo Session(string id, Guid user) => new(sessions.Object, NullLogger.Instance) { Id = id, UserId = user };
        var host = Session("host", owner); var member = Session("member", friend);
        var switched = Session("reassigned", Guid.NewGuid()); var outsider = Session("outside", owner);
        sessions.SetupGet(s => s.Sessions).Returns([host, member, switched, outsider]);
        var updates = new List<(string Session, GroupUpdate<string> Update)>();
        sessions.Setup(s => s.SendSyncPlayGroupUpdate(It.IsAny<string>(), It.IsAny<GroupUpdate<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, GroupUpdate<string>, CancellationToken>((id, update, _) => updates.Add((id, update))).Returns(Task.CompletedTask);
        using var manager = new PrivateSyncPlayManager(new CoreEngine(Mock.Of<ISyncPlayManager>()), store, sessions.Object, () => config);
        var party = store.Create(Guid.NewGuid(), owner, host.Id, GroupMode.Private, 8);
        party.Members.Add(member.Id, friend); party.Members.Add(switched.Id, owner);
        var invite = store.Issue(party, owner, true, 8, null, config).Token;
        Assert.True(store.GuestAdmissionAllowed(invite, null, config)); Assert.Empty(updates);
        Assert.Throws<PartyException>(() => store.RedeemGuest("invalid", null, config)); Assert.Empty(updates);
        var first = store.RedeemGuest(invite, null, config);
        Assert.Equal("Guest 1", store.GuestSession(first, config).Name);
        Assert.Equal(new[] { "host", "member" }, updates.Select(u => u.Session));
        Assert.All(updates, u => { Assert.IsType<SyncPlayUserJoinedUpdate>(u.Update); Assert.Equal(party.Id, u.Update.GroupId); Assert.Equal("Guest 1", u.Update.Data); });
        store.RedeemGuest(invite, first, config); Assert.Equal(2, updates.Count);
        var second = store.RedeemGuest(invite, null, config); Assert.Equal("Guest 2", store.GuestSession(second, config).Name);
        store.LeaveGuest(first);
        var third = store.RedeemGuest(invite, null, config); Assert.Equal("Guest 3", store.GuestSession(third, config).Name);
        Assert.Equal("Guest 2", store.GuestSession(second, config).Name);
        var other = store.Create(Guid.NewGuid(), owner, outsider.Id, GroupMode.Private, 3);
        var otherGuest = store.RedeemGuest(store.Issue(other, owner, true, 2, null, config).Token, null, config);
        Assert.Equal("Guest 1", store.GuestSession(otherGuest, config).Name);
        Assert.Equal(outsider.Id, updates.Last().Session);
        var count = updates.Count; manager.Dispose();
        store.RedeemGuest(invite, null, config); Assert.Equal(count, updates.Count);
    }

    [Fact] public void FailedNotificationDoesNotFailAdmissionOrNotifyAgainOnRefresh()
    {
        var store = new PartyStore(TimeProvider.System);
        var config = new Configuration { EnableGuestInvites = true, DefaultGuestPermission = true };
        var sessions = new Mock<ISessionManager>();var owner = Guid.NewGuid();
        var host = new SessionInfo(sessions.Object, NullLogger.Instance) { Id = "host", UserId = owner };
        sessions.SetupGet(s => s.Sessions).Returns([host]);
        sessions.Setup(s => s.SendSyncPlayGroupUpdate(It.IsAny<string>(), It.IsAny<GroupUpdate<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromException(new IOException("Disconnected")));
        using var manager = new PrivateSyncPlayManager(new CoreEngine(Mock.Of<ISyncPlayManager>()), store, sessions.Object, () => config);
        var party = store.Create(Guid.NewGuid(), owner, host.Id, GroupMode.Private, 3);
        var invite = store.Issue(party, owner, true, 2, null, config).Token;
        var guest = store.RedeemGuest(invite, null, config);
        Assert.Same(party, store.GuestParty(guest, config));
        store.RedeemGuest(invite, guest, config);
        sessions.Verify(s => s.SendSyncPlayGroupUpdate(host.Id, It.IsAny<GroupUpdate<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        sessions.SetupGet(s => s.Sessions).Throws(new IOException("Sessions unavailable"));
        var second = store.RedeemGuest(invite, null, config);
        Assert.Equal("Guest 2", store.GuestSession(second, config).Name);
    }
}
