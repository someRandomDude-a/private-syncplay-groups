using Jellyfin.Plugin.PrivateSyncPlay.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.PrivateSyncPlay;

// Called under PartyStore.Gate, after owner authorization. All collections are detached copies.
internal static class PartyManagementSnapshot
{
    internal static WatchPartyManagement Capture(Party party, PartyStore store, ISessionManager sessions, IUserManager users, Configuration config)
    {
        var accounts = sessions.Sessions.Where(s => party.IsMember(s.Id, s.UserId)).GroupBy(s => s.UserId).Select(group =>
        {
            var account = party.Account(group.Key);
            return new WatchPartyAccountParticipant(account.Id, users.GetUserById(group.Key)?.Username ?? "Signed-in participant", group.Count(), group.Key == party.Owner,
                new(account.ControlPlayback, account.EditQueue), new(party.CanControl(group.Key), party.CanEditQueue(group.Key)));
        }).ToArray();
        var allowed = GuestPlayerPolicy.Features.Where(config.GuestPlayer.Allows).ToArray();
        var leases = store.Guests(party.Id);
        var guests = leases.Select(g => new WatchPartyGuestParticipant(g.Id, "Guest " + g.Id.ToString("N")[..6], leases.Count(other => other.InviteId == g.InviteId))).ToArray();
        var blocked = party.Accounts.Values.Where(a => party.BlockedUsers.Contains(a.UserId))
            .Select(a => new WatchPartyBlockedAccount(a.Id, users.GetUserById(a.UserId)?.Username ?? "Removed account")).ToArray();
        var policy = new WatchPartyPolicy(party.Policy.AllowJoins, party.Policy.AllowGuests, party.Policy.ParticipantsControlPlayback,
            party.Policy.ParticipantsEditQueue, party.Capacity);
        return new(party.Id, policy, party.Members.Count + leases.Length, allowed, accounts, guests, blocked);
    }
}
