# Private SyncPlay plugin API v1

This is an **in-process .NET API**, not a loopback HTTP API. It follows Jellyfin's [plugin service registration and constructor injection pattern](https://github.com/jellyfin/jellyfin-plugin-template#4b-use-plugin-aimed-interfaces-to-add-custom-functionality).

## Reference and register the client

Requires Jellyfin 12 and .NET 10. Install Private SyncPlay normally on the server. Download `PrivateSyncPlay.Api-1.0.0.zip` from the same GitHub release, and add the DLL/XML to your development references:

```xml
<Reference Include="Jellyfin.Plugin.PrivateSyncPlay.Api">
  <HintPath>lib/Jellyfin.Plugin.PrivateSyncPlay.Api.dll</HintPath>
  <Private>true</Private>
</Reference>
```

Include **this API DLL** in your plugin package. Do **not** bundle the main Private SyncPlay plugin, Jellyfin assemblies or another SyncPlay engine. During development in a checkout you can instead use a `ProjectReference` to `src/Jellyfin.Plugin.PrivateSyncPlay.Api`.

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

Inject `IWatchPartyService` into your controllers/hosted services. Register services only in `RegisterServices`; **do not resolve them or call the API during registration**. Check `IsAvailable` after Jellyfin startup and handle `WatchPartyException` with status 503 when Private SyncPlay is absent/disabled. The example is optional-dependency friendly; it does not require the provider to be loaded first.

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

## Methods

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

## Why a client adapter instead of injecting another plugin's implementation?

Jellyfin 12 uses a separate `AssemblyLoadContext` for each plugin. Two bundled copies of an interface DLL are not necessarily the same runtime type. `AddPrivateSyncPlayClient()` registers the interface/client **in your plugin's context**, using normal constructor injection. Its adapter invokes a versioned keyed DI delegate built exclusively from shared framework types:

```text
Key: Jellyfin.Plugin.PrivateSyncPlay.Api.v1
Func<Guid, string, string, string, CancellationToken, Task<string>>
Arguments: actor user ID, active session ID, operation, JSON options, cancellation
```

The transport is an in-memory call with versioned JSON DTOs; **no HTTP, reflection, account token or shared mutable PartyStore** is involved. Do not register/replace the provider delegate yourself. Breaking changes will use a new key/API major version. Prefer the typed client over constructing protocol payloads.

## Compilable integration example

[`examples/WatchPartyConsumer`](https://github.com/someRandomDude-a/private-syncplay-groups/tree/main/examples/WatchPartyConsumer) is a separate example plugin, deliberately built against **only the SDK and Jellyfin**, with its own bundled API DLL. It shows service registration, deriving an actor from Jellyfin claims/active sessions, creating/reusing a party, generating a link and revoking it. It is **not** included in the normal plugin ZIP. Its HTTP endpoints are test/example integration surfaces, not additional production plugin endpoints.

```powershell
dotnet build examples/WatchPartyConsumer -c Release
node scripts/integration.mjs --with-consumer
```

The integration test installs the example in a separate plugin folder to exercise real Jellyfin assembly isolation and checks guest permissions, revocation and final-member teardown through the generated invitation.
