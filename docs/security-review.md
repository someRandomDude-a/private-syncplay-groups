# Security and failure-containment review

This review covers the SyncPlay wrapper, plugin registration/maintenance, guest admission and HLS delivery, native stylesheet discovery and cross-plugin SDK. It is not a security certification or a promise that an in-process plugin can never crash its host.

## Findings addressed

| Risk | Change |
| --- | --- |
| Registration/conflict exceptions could abort startup; uncaught `BackgroundService` exceptions could stop Jellyfin | Disable only this plugin, with a latched unavailable state and administrator log; catch maintenance/dependency failures at the hosted-service boundary. Unsupported engines are not replaced. |
| Cleanup could throw before revoking access | Revoke party/invite/guest state first. Isolate native leave errors; prevent playback commands against orphaned, untracked groups. Encoder cleanup failures disable further plugin access. |
| A disconnected or disabled last signed-in member could permit media until maintenance caught up | Revalidate active, enabled, playback/SyncPlay-authorized signed-in participation on every state/media request; close the party if none remain. |
| Each party could launch an encoder without a server-wide bound | Default limit of two concurrent guest jobs, configurable 1–32, enforced on launch and lowered-limit maintenance; bounded retry backoff after launch failure. |
| SameSite cookies alone do not distinguish sibling origins | Check supplied Origin and Fetch Metadata on guest reads, as well as mandatory same-origin HTTPS redemption/leave. Never trust these headers as authentication. |
| Growing segment files could exceed the initial size check during `CopyTo` | Fixed-size bounded allocation/read: 16 MiB per segment, 256 KiB per manifest. No client-controlled source paths or FFmpeg arguments. |
| Unexpected errors could reveal paths/provider diagnostics to guests | Generic 503 error boundary on plugin controllers; logs record error types, not guest secrets or FFmpeg arguments. No fallback to normal Jellyfin playback endpoints. |
| Malformed installed web configuration or SDK provider replies could throw unexpectedly | Type/size checks and safe CSS/theme fallback; SDK activation/protocol failures become stable unavailable errors. |
| Invalid null configuration entries could throw during validation | Reject invalid settings explicitly; invalid loaded settings disable plugin features during safety checks. |

## Regression coverage

- Unit tests exercise incompatible registration without replacing the existing engine, hosted-service dependency failure without faulting its host task, lease revocation, pre-controller 503 rejection, native cleanup failure and orphan-control rejection.
- Guest tests cover session/permission loss before maintenance, disposal, throttling, HTTPS/origin/sibling-origin restrictions, admission limits, expiry/revocation, and absence of normal Jellyfin authentication or guest playback-control handlers.
- SDK tests cover policy/identity checks, malformed provider replies, cancellation and isolated plugin assembly contexts.
- Real Jellyfin Docker integration generates actual remux/transcode HLS, validates ordinary API denial with guest credentials, injects an unavailable FFmpeg executable, verifies generic failure responses and continued server responsiveness, restores FFmpeg and verifies recovery. Only an isolated disposable container is modified.

## Boundaries and remaining deployment checks

- Owner management requires an authenticated owner session belonging to the exact target party. Roster entries use random party-scoped IDs, never guest credentials or core session IDs. Native SyncPlay playback/queue routes go through the same permission checks as plugin UI actions. Unknown playback actions fail closed. Read-only signed-in sessions are forced out of group wait and their buffering/readiness/ignore-wait requests are not forwarded as an indirect transport bypass.
- Account removal revokes membership before native cleanup and blocks the account across devices and all join paths for this party. Guest removal revokes the admission invitation and all guest leases sharing it; the UI explicitly confirms the affected count. Anonymous identity across new browsers/invitations cannot be established. A native cleanup or wait-policy failure closes the affected party and its guest credentials, rather than keeping partially enforced permissions live.
- Party and per-guest feature choices only narrow the administrator's allowlist. VTT authorization rechecks this full intersection after awaiting conversion. Disabling party guest access revokes guest-enabled invitations, not just the current UI. No role edit grants general Jellyfin permissions or guest SyncPlay commands. Only the owner/admin may end the party; ownership does not confer server administration.

- The guest player has a closed feature allowlist and closed server operation allowlist. Unknown actions, malformed/duplicate declarative plugin controls, and newly introduced controller actions are denied rather than being granted by an absence of deny rules. Disabled text-caption permission is checked before and after asynchronous delivery; UI visibility is not its authorization boundary.
- The visual designer runs the actual guest adapter with a generated, public sample inside `sandbox="allow-scripts"` (no same-origin permission). Its CSP disallows API connections and other origins may not frame it. A frame-bound MessageChannel carries only validated layout descriptors and selection/reorder events, never account tokens or library media. The administrator-only catalog endpoint and normal Jellyfin configuration API retain native elevation requirements. The preview is intentionally public, contains no configuration secrets, and cannot redeem a guest seat.
- Only declarative contributions mapped to existing local actions are supported. No automatic loading of another plugin's JavaScript or the authenticated Jellyfin application is allowed. UI-only volume/fullscreen restrictions cannot control a viewer's browser/device or already-delivered bytes; guest credentials still grant no normal Jellyfin privileges.

- Subtitle delivery is tied to the party, current generation, current item, allowed local track and owner permissions. Text conversion has a separate two-operation concurrency limit, 30-second cancellation deadline, 2 MiB output cap and bounded short-lived cache; credentials/item access are checked again after awaiting conversion. Styled/bitmap subtitles are part of the single shared native video job, not a new encoder per guest. Browser caption selection never changes the signed-in group's playback settings.
- Account invitations use a clean Jellyfin entry URL and short-lived, server-scoped session storage, not a router hash containing a bearer secret. The account API retains its native user/media checks. The browser waits for the server-side realtime session before joining, never sends the invite to a different configured server, and leaves navigation to the normal GroupJoined/queue handlers.

- A request authorized immediately before revocation may already have delivered bytes; buffered/downloaded media cannot be recalled or made unrecordable.
- A guest knows the public server address. Existing anonymous Jellyfin endpoints remain anonymous; this plugin does not firewall or harden unrelated server endpoints/plugins.
- In-process plugins are trusted code, not sandboxes. Other plugins must authenticate their own callers and handle `WatchPartyException`/cancellation. A malicious plugin can bypass shared-process protections.
- FFmpeg uses the server's OS permissions. Codec vulnerabilities, OS failures, out-of-memory conditions, and unkillable processes cannot be fully contained by .NET exception handling. Use an unprivileged account/container, read-only media and resource limits; keep server and encoder patched.
- Guest jobs use native encoding choices but have a separate lifecycle from Jellyfin's ordinary transcode scheduler. Size the guest-job cap conservatively for the hardware and existing load.
- Only the latest stable Jellyfin is supported: currently 12.2/.NET 10, SDK 12.2.0 and ABI 12.2.0.0. `Directory.Build.props` defines the target; runtime checks disable this plugin on other server releases without replacing their engine. Arbitrary third-party SyncPlay replacements are not supported.
- Native upgrade tests install an older plugin package on the latest server and verify native version selection/ABI filtering, automatic update, restart activation, retained settings and invalidated guest credentials. CI verifies the target is still the official latest stable release. New server releases require an explicit target update and successful tests; there is no older-server fallback.
- Physical GPU execution/HDR tone mapping, reverse-proxy forwarded-header trust/TLS behavior, and the administrator's chosen skins still require deployment-specific verification.
