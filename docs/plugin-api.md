# Private SyncPlay integration SDK 1.1

This is an **in-process .NET API**, not a loopback HTTP API. It follows Jellyfin's [plugin service registration and constructor injection pattern](https://github.com/jellyfin/jellyfin-plugin-template#4b-use-plugin-aimed-interfaces-to-add-custom-functionality).

## Choose an integration surface

| Goal | Recommended surface |
| --- | --- |
| Create/query parties; create/revoke invitations; join/leave/end | Inject `IWatchPartyService` |
| Owner roster, party policy, account/guest permissions, kick/block/unblock | The same `IWatchPartyService` |
| Contribute an approved local guest control | `AddPrivateSyncPlayGuestControl(...)` during registration |
| Signed-in playback/queue and public group discovery | Jellyfin's native SyncPlay APIs; Private SyncPlay retains the engine and enforces party roles |
| Internal .NET automation/event/scheduled-job workflow | A normal C# service or function using `IWatchPartyService` |

Never reference the main implementation DLL, reflect into its internals, mutate `PartyStore`, replace its SyncPlay engine, or use guest cookies as account authentication. Server plugins should prefer typed DI clients over loopback HTTP.

## Reference and register the client

Requires the supported latest stable Jellyfin (currently 12.2) and .NET 10. Install Private SyncPlay normally on the server. Use `PrivateSyncPlay.Api-1.1.0.zip` from the matching plugin release (or this checkout's build artifacts while 0.1.3 is an unpublished preview), and add the DLL/XML to your development references:

```xml
<Reference Include="Jellyfin.Plugin.PrivateSyncPlay.Api">
  <HintPath>lib/Jellyfin.Plugin.PrivateSyncPlay.Api.dll</HintPath>
  <Private>true</Private>
</Reference>
```

Include **this API DLL** in your plugin package. Do **not** bundle the main Private SyncPlay plugin, Jellyfin assemblies or another SyncPlay engine. During development in a checkout you can instead use a `ProjectReference` to `src/Jellyfin.Plugin.PrivateSyncPlay.Api`.

The developer ZIP also includes the XML IntelliSense documentation, this guide, pinned build properties and a **portable compilable consumer example**. After extracting, `dotnet build examples/WatchPartyConsumer -c Release` builds against the included public SDK DLL; no checkout or main plugin assembly is needed to compile it. Running the example still requires the installed provider on an isolated Jellyfin test server.

```csharp
using Jellyfin.Plugin.PrivateSyncPlay.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

public sealed class Registrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
        => services.AddPrivateSyncPlayClient();
}
```

Inject the single `IWatchPartyService` into a C# service, controller, hosted service, event handler or automation workflow. It includes all party, invitation, permission and moderation methods. Register services only in `RegisterServices`; **do not resolve them or call the API during registration**. `IsAvailable` means the matching current provider is registered, not that startup/health checks passed. Still handle `WatchPartyException` with status 503 when Private SyncPlay is absent/disabled. No HTTP, JavaScript, raw JSON or separate management client is required by a .NET caller. Do not fall back to privileged HTTP, reflection, or a fabricated session.

### Current API only

- This is the new system's **single current contract**, SDK **1.1.0** for the **0.1.3** plugin being prepared here. Use the matching SDK/provider together. There are no legacy interfaces, older-provider branches, old-key fallbacks, or compatibility matrix.
- Each consuming plugin bundles its matching SDK DLL. A small in-process adapter handles Jellyfin's separate assembly load contexts; this is a .NET function call, not a web/loopback API or a legacy compatibility layer.
- Only the supported latest Jellyfin/.NET target is supported. Missing/mismatched/disabled providers fail closed with 503; update the caller/provider instead of bypassing checks.

## Generate a link on behalf of a user

```csharp
public sealed class MyPartyFeature(IWatchPartyService parties)
{
    public async Task<Uri> InviteAsync(
        Guid authenticatedUserId, string activeSessionId,
        Uri configuredPublicServerUrl, CancellationToken cancellationToken)
    {
        if (!parties.IsAvailable)
            throw new InvalidOperationException("Install and enable Private SyncPlay first.");

        var options = new CreateInviteOptions(
            configuredPublicServerUrl, // e.g. https://media.example.com/jellyfin/
            AllowGuests: false,        // true requires the administrator's guest permission
            MaxUses: 2,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(2));
        var existing = await parties.GetCurrentPartyAsync(
            authenticatedUserId, activeSessionId, cancellationToken);
        WatchPartyInvite invite;
        if (existing is null)
        {
            var created = await parties.CreatePartyWithInviteAsync(
                authenticatedUserId, activeSessionId,
                new CreatePartyOptions("Movie night", WatchPartyVisibility.Private, MaxViewers: 4),
                options, cancellationToken);
            invite = created.Invite;
        }
        else
        {
            // Fails if this user is merely a participant rather than the owner.
            invite = await parties.CreateInviteAsync(authenticatedUserId, activeSessionId,
                existing.PartyId, options, cancellationToken);
        }
        return invite.InviteUrl; // Share deliberately. Never put this bearer URL in logs.
    }
}
```

Derive the actor from **your authenticated request or explicit trusted server-side delegation**, not an arbitrary client-provided `UserId`. Obtain an existing session from `ISessionManager.Sessions` and verify that it belongs to that actor. This API checks the match again, checks that the account is enabled and has SyncPlay access, and never creates a login, hidden host or synthetic session. A user with no active session must open/sign in to Jellyfin before another plugin can create a party for them.

Use a trusted administrator-configured **public** server URL, including Jellyfin's Base URL prefix. HTTP(S) only; no username/password, query string or fragment. Guest invitations require HTTPS. The API does not guess proxy URLs from untrusted Host headers. Null visibility/concurrent/use limits inherit administrator settings; explicit disallowed values are rejected, never silently elevated.

## Party methods: `IWatchPartyService`

All methods take `(Guid userId, string sessionId, ..., CancellationToken cancellationToken = default)` and return `Task`/`Task<T>`. Public types include XML documentation, distributed with the SDK for IntelliSense.

| Method | Additional arguments | Result / behavior |
| --- | --- | --- |
| `GetCurrentPartyAsync` | none | `WatchPartyInfo?`; only that session's party; private code visible to its owner only |
| `CreatePartyAsync` | `CreatePartyOptions` | `WatchPartyInfo`; requires native create permission; rejects an already-joined session |
| `CreatePartyWithInviteAsync` | `CreatePartyOptions`, `CreateInviteOptions` | `WatchPartyWithInvite`; validates invite policy/URL/limits **before** creating the party |
| `CreateInviteAsync` | `Guid partyId`, `CreateInviteOptions` | `WatchPartyInvite`; caller must own and participate in the party |
| `RevokeInviteAsync` | `Guid partyId`, `Guid inviteId` | Revokes the owner's link and its guest leases; idempotent for an unknown invite ID |
| `JoinByCodeAsync` | `string code` | Native media restrictions and concurrent limits still apply |
| `JoinByInviteAsync` | `string token` | Use the secret URL fragment; applies media restrictions, redemption and concurrent limits |
| `LeavePartyAsync` | none | Removes the session; final authenticated member leaving destroys all guest access |
| `EndPartyAsync` | `Guid partyId` | Owner or server admin only; removes authenticated members and guests |

Errors use `WatchPartyException.StatusCode`: **400** invalid input, **403** policy/actor mismatch, **404** unavailable party/invite, **409** full/already joined, **429** join throttling, **503** provider unavailable. Cancellation is cooperative before mutation. Once an atomic creation/invitation operation commits, use the returned IDs to explicitly revoke/end it rather than assuming a canceled caller rolled back server state. Do not automatically retry creation after an ambiguous caller failure; check `GetCurrentPartyAsync` first.

The server's master restrictions, per-user overrides, media permissions, party lifetime and guest-isolation rules are not bypassable API options. In-process Jellyfin plugins are trusted code, not sandboxed applications; a caller remains responsible for authenticating/authorizing its own external entry points.

Party owner moderation also applies to these methods: locked/blocked joins fail through `JoinByCodeAsync` and `JoinByInviteAsync`; creating guest invitations fails when the owner has disabled party guest access.

## Owner methods on the same `IWatchPartyService`

All calls take `(Guid userId, string sessionId, Guid partyId, ..., CancellationToken cancellationToken = default)`. They require the **enabled owner in an active session belonging to that exact party**, not just an administrator or arbitrary participant. Ending is intentionally on `IWatchPartyService` and permits owner/admin. Every call uses the same policy/permission guards as the web UI.

| Method | Additional arguments | Behavior |
| --- | --- | --- |
| `GetManagementAsync` | none | `WatchPartyManagement` snapshot: policy, allowed/effective guest features, accounts, guest leases, blocked accounts |
| `UpdatePolicyAsync` | `WatchPartyPolicy` | Complete policy replacement within administrator limits; does not reset per-participant overrides |
| `SetAccountPermissionsAsync` | participant ID, `WatchPartyAccountPermissions` | Apply to all devices of that account in this party; queue editing also requires playback permission |
| `SetGuestPermissionsAsync` | participant ID, `WatchPartyGuestPermissions` | Narrow optional guest features; `new()` inherits party defaults; `new([])` disables all optional features |
| `KickAccountAsync` | account participant ID | Remove all devices and block rejoining this party; guest IDs are rejected |
| `KickGuestAndRevokeInviteAsync` | guest participant ID | Revoke its invitation and disconnect **all guest leases sharing it**; account IDs are rejected |
| `UnblockAccountAsync` | blocked-account participant ID | Allow rejoining, without auto-joining or resetting permissions |

Snapshots contain **opaque party participant IDs**, not Jellyfin user IDs, core session IDs, invite tokens or guest credentials. Use `snapshot.Accounts`, `snapshot.Guests` and `snapshot.BlockedAccounts` to find targets; do not guess IDs or treat a guest label as a verified identity. `Permissions` are stored overrides; `EffectivePermissions` / `EffectiveFeatures` include current ceilings. Account entries aggregate devices; guest entries represent current leases. `SharedInviteGuestCount` is a snapshot, not a reservation of the affected count.

### Read-modify-write policy and permissions

```csharp
public sealed class MyOwnerTools(IWatchPartyService parties)
{
    public async Task LockAsync(Guid authenticatedOwner, string existingSession,
        Guid partyId, CancellationToken ct)
    {
        if (!parties.IsAvailable)
            throw new InvalidOperationException("The current Private SyncPlay .NET API is unavailable.");

        var state = await parties.GetManagementAsync(authenticatedOwner, existingSession, partyId, ct);
        // Preserve capacity, guest policy and other fields rather than constructing accidental defaults.
        await parties.UpdatePolicyAsync(authenticatedOwner, existingSession, partyId,
            state.Policy with { AllowJoins = false }, ct);

        var account = state.Accounts.FirstOrDefault(p => !p.IsOwner);
        if (account is not null)
            await parties.SetAccountPermissionsAsync(authenticatedOwner, existingSession, partyId,
                account.ParticipantId, new(ControlPlayback: false, EditQueue: false), ct);

        var guest = state.Guests.FirstOrDefault();
        if (guest is not null)
        {
            // Only choose from the currently effective party allowlist. These are not SyncPlay privileges.
            var allowed = state.EffectiveGuestFeatures
                .Where(f => f is GuestPlayerFeatures.Volume or GuestPlayerFeatures.Subtitles).ToArray();
            await parties.SetGuestPermissionsAsync(authenticatedOwner, existingSession, partyId,
                guest.ParticipantId, new(allowed), ct);
            // To reset inheritance later, pass new WatchPartyGuestPermissions(), not null options.
        }
    }
}
```

Before calling `KickGuestAndRevokeInviteAsync`, obtain a fresh snapshot and show a confirmation explaining that the invitation itself is revoked and **all** `SharedInviteGuestCount` sessions using it are removed. The count may change before execution. A new browser/newly issued link cannot be tied reliably to a previously removed anonymous person. Use separate admission links for independently removable guests. Account removal is a party-local account block, not a Jellyfin account ban.

## Lifecycle, concurrency and failure handling

- Parties, overrides, participant IDs, blocks, invitation tokens and guest leases are in memory. Party closure or server restart invalidates them; never persist these as durable account permissions or long-lived identities.
- Returned snapshots are detached. Mutating a returned array/list cannot edit a live party. Explicit commands revalidate actors/targets and policy at execution; stale/disconnected targets may return 404. A rejected type-specific kick never falls back to another participant type.
- Policy replacement is serialized server-side but is **last writer wins**, not an optimistic-concurrency/ETag API. Serialize related edits in your integration and refetch before confirming/saving. Do not blindly replay old complete snapshots over newer owner changes.
- Cancellation is checked before committing an operation. Once mutation commits, cancellation is not rollback. In particular, revocation/kick cannot be undone by retrying; unblock does not restore sessions/permissions. Never automatically retry creation/invitation after an ambiguous transport/provider failure; query current state first.
- A native removal/wait-policy failure can return 503 **after ending the affected party for safety**. Refetch, do not assume failure means no effect. Never retry by granting broader rights or removing enforcement.
- There is no public plugin event/subscription contract yet. Refetch after commands and poll conservatively for a management dashboard. Normal Jellyfin SyncPlay clients continue receiving their native group/queue events.
- Treat labels as plain text in your UI. Never log invitation bearer URLs, join codes, account tokens or guest cookies. Do not request/reuse guest credentials through these APIs; guests deliberately have no Jellyfin identity.

For server-plugin entry points using native SyncPlay APIs, still authenticate the actor and enforce Jellyfin's native request policies. The registered SyncPlay manager enforces party-specific transport/queue restrictions; do not bypass it via an internal core engine or ordinary session-progress reports. No SDK option grants server administration, library access or guest playback controls.

## Why a client adapter instead of injecting another plugin's implementation?

Jellyfin 12 uses a separate `AssemblyLoadContext` for each plugin. Two bundled copies of an interface DLL are not necessarily the same runtime type. `AddPrivateSyncPlayClient()` registers the interface/client **in your plugin's context**, using normal constructor injection. Its adapter invokes a versioned keyed DI delegate built exclusively from shared framework types:

```text
Key: Jellyfin.Plugin.PrivateSyncPlay.Api.1.1
Func<Guid, string, string, string, CancellationToken, Task<string>>
Arguments: actor user ID, active session ID, operation, JSON options, cancellation
```

This one current key serves **all** operations. Its in-memory serialization is solely a load-context implementation detail: .NET scripts use the typed methods and DTOs shown above. **No HTTP, reflection, account token or shared mutable PartyStore** is involved. Do not register/replace the provider delegate yourself, build raw protocol payloads, or attempt older keys. Consumers and the provider must use the matching current API.

## Guest-safe player controls

Player integrations may register a declarative control from `RegisterServices`:

```csharp
services.AddPrivateSyncPlayGuestControl(new GuestPlayerControl(
    "example.captions", "subtitles", "Example plugin captions", "closed_caption"));
```

Use a stable, namespaced lowercase ID (letters/digits/dots/hyphens, max 64 characters), a plain-text label (max 80 characters), and one Material icon class name (max 48 characters). Supported actions are exactly `GuestPlayerFeatures.Volume`, `.Mute`, `.Subtitles`, and `.Fullscreen`. A control **does not grant a feature** or become enabled just because its plugin is installed: an administrator must add it in **Guest player designer**, and its associated feature must be explicitly allowed by administrator, party and guest policy. Duplicate IDs, invalid descriptors and unsupported actions are ignored. Missing plugins or unknown saved IDs render nothing. Register only during startup; contributions are not a runtime code-execution surface.

The registry uses keyed DI **strings** (`GuestPlayerUiExtensions.ServiceKey`, `Jellyfin.Plugin.PrivateSyncPlay.GuestUi.v1`) so each plugin may bundle its own SDK copy without losing type identity. Controls clone the guest adapter's actual native control DOM and bind a built-in local action. HTML, CSS/script URLs, callbacks, arbitrary server operations and authenticated web-player scripts are not supported by this interface. Use public server branding CSS for trusted theme changes; never include private tokens or media URLs in CSS. This preserves the no-account boundary and lets the same contributed controls render in both real guests and the sandboxed editor.

## Compilable integration example

[`examples/WatchPartyConsumer`](https://github.com/someRandomDude-a/private-syncplay-groups/tree/main/examples/WatchPartyConsumer) is a separate example plugin, deliberately built against **only the SDK and Jellyfin**, with its own bundled API DLL. Its [README](https://github.com/someRandomDude-a/private-syncplay-groups/blob/main/examples/WatchPartyConsumer/README.md) (also included at `examples/WatchPartyConsumer/README.md` in the SDK ZIP) covers installation, actor resolution, creation/revocation, owner policy/permissions, account and guest removal, unblocking, and a declarative guest control. It is **not** included in the normal plugin ZIP. Its HTTP endpoints are test/example integration surfaces, not additional production plugin endpoints. The demo's client-supplied public URL is for disposable tests; use trusted administrator configuration in production.

```powershell
dotnet build examples/WatchPartyConsumer -c Release
node scripts/integration.mjs --with-consumer
```

The integration test installs the example in a separate plugin folder to exercise real Jellyfin assembly isolation and checks guest permissions, revocation and final-member teardown through the generated invitation.

## Internal .NET scripting / automation

`examples/WatchPartyConsumer/PartyAutomation.cs` is a compilable C# workflow with **no HTTP or JavaScript dependency**. It creates private parties/invitations, changes participant policy, and resets guest permissions using the same current interface. Register your own workflow normally:

```csharp
services.AddPrivateSyncPlayClient();
services.AddSingleton<PartyAutomation>();
```

Inject it into a server-side event handler, hosted service or scheduler and call:

```csharp
var created = await automation.CreateAsync(ownerUserId, activeSessionId, trustedPublicServerUrl, ct);
await automation.MakeParticipantsReadOnlyAsync(ownerUserId, activeSessionId, created.Party.PartyId, ct);
// Share created.Invite.InviteUrl deliberately; never log the secret URL.
```

`ownerUserId` and `activeSessionId` must represent an explicitly authorized actor and a real existing session. Automation does not fabricate a host or keep a party alive without signed-in participants. Internal .NET code is trusted server code, but its external entry points must still authenticate/authorize requests. The SDK applies the party/administrator policy again.

The plugin's HTTP controllers support its web UI. They are not required for internal scripting and are not a compatibility interface for old SDKs. The example controller merely lets the disposable integration fixture exercise .NET calls inside another plugin's load context.
