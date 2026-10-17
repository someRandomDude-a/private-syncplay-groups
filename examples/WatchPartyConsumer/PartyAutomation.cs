using Jellyfin.Plugin.PrivateSyncPlay.Api;

namespace WatchPartyConsumer;

// A normal server-side .NET workflow: inject it into an event handler or hosted service.
// This code performs no HTTP requests and does not need a browser or JavaScript.
public sealed class PartyAutomation(IWatchPartyService parties)
{
    public Task<WatchPartyWithInvite> CreateAsync(Guid authenticatedOwner, string existingSession, Uri trustedPublicServerUrl, CancellationToken token = default)
        => parties.CreatePartyWithInviteAsync(authenticatedOwner, existingSession,
            new CreatePartyOptions("Automated movie night", WatchPartyVisibility.Private, 4),
            new CreateInviteOptions(trustedPublicServerUrl, AllowGuests:false, MaxUses:2), token);

    public async Task MakeParticipantsReadOnlyAsync(Guid authenticatedOwner, string existingSession, Guid partyId, CancellationToken token = default)
    {
        var state = await parties.GetManagementAsync(authenticatedOwner, existingSession, partyId, token);
        await parties.UpdatePolicyAsync(authenticatedOwner, existingSession, partyId,
            state.Policy with { ParticipantsControlPlayback = false, ParticipantsEditQueue = false }, token);
    }

}
