# Compilable server-plugin integration

This **example/test plugin is not included in the production plugin ZIP**. It references only the public SDK and Jellyfin, bundles its own SDK DLL, and is tested in Jellyfin's separate plugin load context. Do not install it on an existing production server for testing.

## Copy the integration pattern

1. Reference SDK **1.1.0** (`Jellyfin.Plugin.PrivateSyncPlay.Api.dll`) in your plugin and bundle that SDK DLL, not the main plugin or Jellyfin assemblies.
2. In `IPluginServiceRegistrator.RegisterServices`, call `services.AddPrivateSyncPlayClient()`. It registers the single current `IWatchPartyService`. Do not resolve it until after startup.
3. Inject that interface into a .NET service, automation function or event handler. It includes both party and moderation methods. Handle `WatchPartyException` (notably 403/404/409/503) even when `IsAvailable` is true. Use the matching current SDK/provider; there is no legacy-provider support or fallback.
4. Derive the actor from the authenticated request and an **existing session belonging to that same user/device/client**. See `InviteController.Actor()`. Never accept user/session identity from untrusted parameters or create a synthetic session.
5. Use the SDK for operations; do not use reflection, the main plugin's implementation, loopback HTTP, or mutable `PartyStore` objects. Native playback/queue operations remain Jellyfin's SyncPlay API and retain party-role restrictions.

`ConsumerPlugin.cs` also shows an opt-in guest control contribution. Administrators must enable it in the designer. Contributions use native control DOM and existing local actions, not arbitrary scripts.

## What the example demonstrates

**Start with `PartyAutomation.cs` for internal .NET scripting.** It is a normal injectable C# service that creates a private party, changes policy and restores guest defaults. It makes no HTTP requests and requires neither a browser nor JavaScript. Register your workflow with DI and call its methods from an authorized event handler, hosted service or scheduler. The `InviteController` routes below are only a test harness to invoke .NET operations inside this example plugin; they are not the recommended integration interface.

- `POST WatchPartyExample/Invite`: create/reuse the actor's party and generate a link.
- `DELETE WatchPartyExample/Invites/{id}`: revoke a link and its guest leases.
- `GET WatchPartyExample/Management`: a typed owner-only roster/policy snapshot.
- `POST WatchPartyExample/Policy`: complete policy replacement (use a snapshot's `Policy with { ... }` in .NET).
- `POST WatchPartyExample/Accounts/{participant}/Permissions`: all devices of that account in this party.
- `POST WatchPartyExample/Guests/{participant}/Permissions`: narrowed optional features; `{ "features": null }` resets inheritance, `[]` disables all optional features.
- `DELETE WatchPartyExample/Accounts/{participant}`: kick/block the account from this party.
- `DELETE WatchPartyExample/Guests/{participant}/Admission`: revoke its invitation and **all guest leases sharing it**. Confirm this effect in your UI.
- `POST WatchPartyExample/Blocked/{participant}/Unblock`: permit rejoining; does not auto-join or reset permissions.

Every management call derives its party from the caller's current session and is checked again by the provider. Participant IDs come from the management snapshot, **not Jellyfin user IDs, session IDs, or guest credentials**.

**Demo-only URL input:** this example's invite request accepts `ServerBaseUrl` so disposable tests can use their ephemeral port. In a production integration, take that URL from trusted administrator configuration, including Jellyfin's base-path prefix. Never trust an arbitrary client-supplied URL or forwarded Host header. Never log invite URLs, tokens, join codes, or credentials.

## Build/test on an isolated server

The SDK ZIP includes this example and the pinned `Directory.Build.props`. Extract it anywhere, then run `dotnet build examples/WatchPartyConsumer -c Release` from the extracted SDK root. Its project uses the adjacent public SDK DLL when there is no repository ProjectReference. Copy the resulting example DLL and its SDK DLL together only into a disposable test server's plugin folder. A full source checkout is not required to compile the example.

For the full integration fixture, use the repository root with the pinned .NET SDK and Docker:

```powershell
dotnet build examples/WatchPartyConsumer -c Release
./scripts/package.ps1
node scripts/integration.mjs <new-disposable-directory> --with-consumer --install-from-catalog
```

The fixture creates its own server/data and installs the real packaged plugin. It verifies the cross-load-context clients, policy/permission operations, account and shared-link removal, disabled-provider handling, and guest isolation. It never mounts an existing Jellyfin installation.

See [the full SDK guide](../../docs/plugin-api.md) for quick starts, compatibility, exact interfaces, error handling and concurrency/lifetime rules.
