namespace Jellyfin.Plugin.PrivateSyncPlay.Api;

/// <summary>The current typed .NET scripting API for party, invitation, permission and moderation operations.</summary>
/// <remarks>Actors must be derived from the calling plugin's authenticated context. No operation creates a login or synthetic session.</remarks>
public interface IWatchPartyService
{
    /// <summary>Gets whether the matching current API provider is registered. Operations may still fail if it is not ready.</summary>
    bool IsAvailable { get; }

    /// <summary>Gets the caller's current party, or null if its active session is not in a party.</summary>
    /// <param name="userId">Authenticated actor's user ID.</param><param name="sessionId">Actor's existing Jellyfin session ID.</param>
    /// <param name="cancellationToken">Cancellation before the operation mutates state.</param><returns>A snapshot without other parties' secrets.</returns>
    Task<WatchPartyInfo?> GetCurrentPartyAsync(Guid userId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Creates a party for an active session that is not already in another party.</summary>
    /// <param name="userId">Authenticated actor's user ID.</param><param name="sessionId">Actor's existing Jellyfin session ID.</param>
    /// <param name="options">Creation settings; null mode/limit inherit administrator policy.</param><param name="cancellationToken">Cancellation before mutation.</param>
    /// <returns>The new party. Existing membership is never silently replaced.</returns>
    Task<WatchPartyInfo> CreatePartyAsync(Guid userId, string sessionId, CreatePartyOptions options, CancellationToken cancellationToken = default);

    /// <summary>Creates a party and invitation together, validating invite permission/limits before creation.</summary>
    /// <param name="userId">Authenticated actor's user ID.</param><param name="sessionId">Actor's existing Jellyfin session ID.</param>
    /// <param name="party">Party settings.</param><param name="invite">Invite settings, including the trusted public server URL.</param>
    /// <param name="cancellationToken">Cancellation before mutation.</param><returns>The party and its newly generated bearer link.</returns>
    Task<WatchPartyWithInvite> CreatePartyWithInviteAsync(Guid userId, string sessionId, CreatePartyOptions party, CreateInviteOptions invite, CancellationToken cancellationToken = default);

    /// <summary>Generates a bearer invite link for a party owned by the caller.</summary>
    /// <param name="userId">Authenticated actor's user ID.</param><param name="sessionId">Actor's active session, which must belong to this party.</param>
    /// <param name="partyId">Party identifier.</param><param name="options">Invite settings. Guest links require HTTPS and administrator permission.</param>
    /// <param name="cancellationToken">Cancellation before mutation.</param><returns>An invitation; its URL is a secret and must not be logged.</returns>
    Task<WatchPartyInvite> CreateInviteAsync(Guid userId, string sessionId, Guid partyId, CreateInviteOptions options, CancellationToken cancellationToken = default);

    /// <summary>Revokes an owner's invitation and any guest leases admitted by it.</summary>
    /// <param name="userId">Authenticated actor's user ID.</param><param name="sessionId">Actor's active party session.</param><param name="partyId">Party identifier.</param>
    /// <param name="inviteId">Invitation identifier, not its secret token.</param><param name="cancellationToken">Cancellation before mutation.</param><returns>Completion.</returns>
    Task RevokeInviteAsync(Guid userId, string sessionId, Guid partyId, Guid inviteId, CancellationToken cancellationToken = default);

    /// <summary>Joins a private party using its short code, retaining the user's own library restrictions.</summary>
    /// <param name="userId">Authenticated actor's user ID.</param><param name="sessionId">Actor's active Jellyfin session.</param><param name="code">Private group join code.</param>
    /// <param name="cancellationToken">Cancellation before mutation.</param><returns>Completion.</returns>
    Task JoinByCodeAsync(Guid userId, string sessionId, string code, CancellationToken cancellationToken = default);

    /// <summary>Joins using an invite secret, retaining media restrictions and redemption/capacity limits.</summary>
    /// <param name="userId">Authenticated actor's user ID.</param><param name="sessionId">Actor's active Jellyfin session.</param><param name="token">Secret fragment from an invitation URL.</param>
    /// <param name="cancellationToken">Cancellation before mutation.</param><returns>Completion.</returns>
    Task JoinByInviteAsync(Guid userId, string sessionId, string token, CancellationToken cancellationToken = default);

    /// <summary>Leaves a party. The last authenticated member leaving invalidates all guest access.</summary>
    /// <param name="userId">Authenticated actor's user ID.</param><param name="sessionId">Actor's active Jellyfin session.</param>
    /// <param name="cancellationToken">Cancellation before mutation.</param><returns>Completion.</returns>
    Task LeavePartyAsync(Guid userId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Ends a party as its owner or a server administrator.</summary>
    /// <param name="userId">Authenticated actor's user ID.</param><param name="sessionId">Actor's active Jellyfin session.</param><param name="partyId">Party identifier.</param>
    /// <param name="cancellationToken">Cancellation before mutation.</param><returns>Completion.</returns>
    Task EndPartyAsync(Guid userId, string sessionId, Guid partyId, CancellationToken cancellationToken = default);

    /// <summary>Gets a detached owner-only policy/participant snapshot. Requires the owner in an active session in this exact party.</summary>
    /// <param name="userId">Authenticated owner.</param><param name="sessionId">Owner's active party session.</param><param name="partyId">Exact party.</param>
    /// <param name="cancellationToken">Cancellation before the operation starts.</param><returns>A snapshot without invitation tokens or guest credentials.</returns>
    Task<WatchPartyManagement> GetManagementAsync(Guid userId, string sessionId, Guid partyId, CancellationToken cancellationToken = default);

    /// <summary>Replaces party policy without changing administrator settings or per-participant overrides.</summary>
    /// <remarks>Read-modify-write the current Policy with a with-expression. Disabling guests permanently revokes guest-enabled links and their leases.</remarks>
    /// <param name="userId">Authenticated owner.</param><param name="sessionId">Active party session.</param><param name="partyId">Exact party.</param>
    /// <param name="policy">Complete party policy. Null guest features inherit administrator policy.</param><param name="cancellationToken">Cancellation before mutation.</param><returns>Completion.</returns>
    Task UpdatePolicyAsync(Guid userId, string sessionId, Guid partyId, WatchPartyPolicy policy, CancellationToken cancellationToken = default);

    /// <summary>Sets permissions for all devices of one account in this party; cannot demote the owner or bypass party/admin ceilings.</summary>
    /// <param name="userId">Authenticated owner.</param><param name="sessionId">Active party session.</param><param name="partyId">Exact party.</param>
    /// <param name="participantId">Account participant ID from the management snapshot, not a Jellyfin user ID.</param><param name="permissions">Playback/queue permissions; queue editing also requires playback.</param>
    /// <param name="cancellationToken">Cancellation before mutation.</param><returns>Completion.</returns>
    Task SetAccountPermissionsAsync(Guid userId, string sessionId, Guid partyId, Guid participantId, WatchPartyAccountPermissions permissions, CancellationToken cancellationToken = default);

    /// <summary>Narrows optional features for one current guest lease, or resets it to party inheritance.</summary>
    /// <param name="userId">Authenticated owner.</param><param name="sessionId">Active party session.</param><param name="partyId">Exact party.</param>
    /// <param name="participantId">Guest participant ID from the snapshot.</param><param name="options">Null Features inherits; an empty collection disables all optional features.</param>
    /// <param name="cancellationToken">Cancellation before mutation.</param><returns>Completion. Guests never gain account or SyncPlay privileges.</returns>
    Task SetGuestPermissionsAsync(Guid userId, string sessionId, Guid partyId, Guid participantId, WatchPartyGuestPermissions options, CancellationToken cancellationToken = default);

    /// <summary>Removes all of an account's devices and blocks rejoining this party. Does not ban the Jellyfin account.</summary>
    /// <param name="userId">Authenticated owner.</param><param name="sessionId">Active party session.</param><param name="partyId">Exact party.</param>
    /// <param name="participantId">Account participant ID; guest IDs are rejected to avoid accidentally revoking a shared link.</param><param name="cancellationToken">Cancellation before mutation.</param><returns>Completion.</returns>
    Task KickAccountAsync(Guid userId, string sessionId, Guid partyId, Guid participantId, CancellationToken cancellationToken = default);

    /// <summary>Revokes a guest's admission invitation and disconnects ALL guest leases admitted by that same invitation.</summary>
    /// <remarks>Confirm the shared-link effect with the caller. Anonymous identity across new browsers/new invitations cannot be established. Account IDs are rejected.</remarks>
    /// <param name="userId">Authenticated owner.</param><param name="sessionId">Active party session.</param><param name="partyId">Exact party.</param>
    /// <param name="participantId">Guest participant ID from a fresh snapshot.</param><param name="cancellationToken">Cancellation before mutation.</param><returns>Completion; the link cannot admit new guests or accounts.</returns>
    Task KickGuestAndRevokeInviteAsync(Guid userId, string sessionId, Guid partyId, Guid participantId, CancellationToken cancellationToken = default);

    /// <summary>Allows an account to rejoin this party. Does not automatically rejoin it, restore permissions, or redeem a link.</summary>
    /// <param name="userId">Authenticated owner.</param><param name="sessionId">Active party session.</param><param name="partyId">Exact party.</param>
    /// <param name="participantId">ID from BlockedAccounts.</param><param name="cancellationToken">Cancellation before mutation.</param><returns>Completion.</returns>
    Task UnblockAccountAsync(Guid userId, string sessionId, Guid partyId, Guid participantId, CancellationToken cancellationToken = default);
}

/// <summary>Group visibility independent of the server plugin's implementation types.</summary>
public enum WatchPartyVisibility
{
    /// <summary>Browsable through SyncPlay/List.</summary>
    Public,
    /// <summary>Requires a valid code or invite.</summary>
    Private
}

/// <summary>Options for a new party.</summary>
/// <param name="Name">Display name, between 1 and 200 characters.</param><param name="Visibility">Null inherits policy.</param>
/// <param name="MaxViewers">Concurrent session limit including the host; null inherits the server limit.</param>
public sealed record CreatePartyOptions(string Name, WatchPartyVisibility? Visibility = null, int? MaxViewers = null);

/// <summary>Invitation options. Guests are disabled unless explicitly requested and permitted.</summary>
/// <param name="ServerBaseUrl">Trusted public HTTP(S) Jellyfin base URL, including any /jellyfin prefix; no query, fragment or credentials.</param>
/// <param name="AllowGuests">Whether to permit anonymous viewers; requires HTTPS and administrator permission.</param>
/// <param name="MaxUses">Maximum redemptions; null inherits the administrator limit.</param><param name="ExpiresAt">Optional earlier expiration; never outlives the party.</param>
public sealed record CreateInviteOptions(Uri ServerBaseUrl, bool AllowGuests = false, int? MaxUses = null, DateTimeOffset? ExpiresAt = null);

/// <summary>A caller-scoped party snapshot.</summary>
/// <param name="PartyId">Party ID.</param><param name="OwnerUserId">Owner ID.</param><param name="Visibility">Public/private mode.</param>
/// <param name="MaxViewers">Effective concurrent limit.</param><param name="ViewerCount">Current account and guest sessions.</param>
/// <param name="JoinCode">Private code for its owner only; otherwise null. Treat as a secret.</param>
public sealed record WatchPartyInfo(Guid PartyId, Guid OwnerUserId, WatchPartyVisibility Visibility, int MaxViewers, int ViewerCount, string? JoinCode);

/// <summary>A generated invitation. Never log or persist its bearer URL unnecessarily.</summary>
/// <param name="InviteId">Non-secret invitation ID used for revocation.</param><param name="PartyId">Party ID.</param>
/// <param name="InviteUrl">Bearer URL with a secret fragment.</param><param name="AllowGuests">Whether guests may redeem it.</param>
/// <param name="MaxUses">Redemption limit.</param><param name="ExpiresAt">Optional earlier expiration.</param>
public sealed record WatchPartyInvite(Guid InviteId, Guid PartyId, Uri InviteUrl, bool AllowGuests, int MaxUses, DateTimeOffset? ExpiresAt);

/// <summary>A newly created party and its invitation.</summary>
/// <param name="Party">The party snapshot.</param><param name="Invite">The invitation.</param>
public sealed record WatchPartyWithInvite(WatchPartyInfo Party, WatchPartyInvite Invite);

/// <summary>A stable API failure (400 invalid input, 403 policy, 404 missing, 409 conflict, 429 throttled, 503 unavailable).</summary>
/// <param name="statusCode">HTTP-style status usable by calling plugins without depending on ASP.NET types.</param><param name="message">Safe error description.</param>
public sealed class WatchPartyException(int statusCode, string message) : Exception(message)
{
    /// <summary>Gets the failure status code.</summary>
    public int StatusCode { get; } = statusCode;
}
