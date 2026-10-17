using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.PrivateSyncPlay.Api;

/// <summary>Complete party policy. These values can only narrow administrator/Jellyfin permissions.</summary>
/// <param name="AllowJoins">Admit new viewers; current viewers remain connected when false.</param><param name="AllowGuests">Permit guest access; false permanently revokes current guest-enabled invitations and their leases.</param>
/// <param name="ParticipantsControlPlayback">Playback ceiling for non-owner accounts.</param><param name="ParticipantsEditQueue">Queue ceiling; also requires playback permission.</param>
/// <param name="MaxViewers">Concurrent sessions including the owner, within administrator limits and covering current viewers.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WatchPartyPolicy(bool AllowJoins, bool AllowGuests, bool ParticipantsControlPlayback, bool ParticipantsEditQueue, int MaxViewers);

/// <summary>Account permission overrides. Effective queue editing also requires playback permission and both party ceilings.</summary>
/// <param name="ControlPlayback">Permit transport control.</param><param name="EditQueue">Permit queue editing.</param>
public sealed record WatchPartyAccountPermissions(bool ControlPlayback, bool EditQueue);

/// <summary>Account participant snapshot, aggregated across devices in this party.</summary>
/// <param name="ParticipantId">Opaque party-scoped ID.</param><param name="Name">Plain-text account display name.</param><param name="SessionCount">Active devices in this party.</param>
/// <param name="IsOwner">Whether this is the immutable owner.</param><param name="Permissions">Stored account overrides.</param><param name="EffectivePermissions">Overrides intersected with party ceilings; owner retains control.</param>
public sealed record WatchPartyAccountParticipant(Guid ParticipantId, string Name, int SessionCount, bool IsOwner, WatchPartyAccountPermissions Permissions, WatchPartyAccountPermissions EffectivePermissions);

/// <summary>Anonymous current guest lease snapshot; never a verified human identity.</summary>
/// <param name="ParticipantId">Opaque lease reference, not its credential.</param><param name="Name">Generated plain-text label.</param>
/// <param name="SharedInviteGuestCount">Current leases affected by revoking this guest's invitation; may change after this snapshot.</param>
public sealed record WatchPartyGuestParticipant(Guid ParticipantId, string Name, int SharedInviteGuestCount);

/// <summary>An account blocked from this party only.</summary>
/// <param name="ParticipantId">Opaque ID to pass to UnblockAccountAsync.</param><param name="Name">Plain-text account label.</param>
public sealed record WatchPartyBlockedAccount(Guid ParticipantId, string Name);

/// <summary>Detached owner-only management snapshot. Mutating a returned collection never changes the live party.</summary>
/// <param name="PartyId">Party reference.</param><param name="Policy">Stored policy, suitable for a read-modify-write update.</param><param name="ViewerCount">Account and guest sessions.</param>
/// <param name="GuestFeatures">Read-only snapshot of administrator-configured optional guest features. Owners cannot override these.</param>
/// <param name="Accounts">Active account participants.</param><param name="Guests">Current anonymous leases.</param><param name="BlockedAccounts">Accounts explicitly blocked from this party.</param>
public sealed record WatchPartyManagement(Guid PartyId, WatchPartyPolicy Policy, int ViewerCount, IReadOnlyList<string> GuestFeatures,
    IReadOnlyList<WatchPartyAccountParticipant> Accounts, IReadOnlyList<WatchPartyGuestParticipant> Guests, IReadOnlyList<WatchPartyBlockedAccount> BlockedAccounts);

/// <summary>Stable feature names for administrator-configured guest controls. Unknown features are never granted.</summary>
public static class GuestPlayerFeatures
{
    /// <summary>Current video title; the server suppresses it when disallowed.</summary>
    public const string Title = "title";
    /// <summary>Read-only playback position display; synchronization timestamps remain necessary.</summary>
    public const string Position = "position";
    /// <summary>Local browser volume control, not a server-enforceable device volume restriction.</summary>
    public const string Volume = "volume";
    /// <summary>Local mute control.</summary>
    public const string Mute = "mute";
    /// <summary>Optional text subtitle selection/delivery, enforced server-side.</summary>
    public const string Subtitles = "subtitles";
    /// <summary>Local fullscreen control, subject to browser permissions.</summary>
    public const string Fullscreen = "fullscreen";
}
