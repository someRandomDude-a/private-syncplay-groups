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

- A request authorized immediately before revocation may already have delivered bytes; buffered/downloaded media cannot be recalled or made unrecordable.
- A guest knows the public server address. Existing anonymous Jellyfin endpoints remain anonymous; this plugin does not firewall or harden unrelated server endpoints/plugins.
- In-process plugins are trusted code, not sandboxes. Other plugins must authenticate their own callers and handle `WatchPartyException`/cancellation. A malicious plugin can bypass shared-process protections.
- FFmpeg uses the server's OS permissions. Codec vulnerabilities, OS failures, out-of-memory conditions, and unkillable processes cannot be fully contained by .NET exception handling. Use an unprivileged account/container, read-only media and resource limits; keep server and encoder patched.
- Guest jobs use native encoding choices but have a separate lifecycle from Jellyfin's ordinary transcode scheduler. Size the guest-job cap conservatively for the hardware and existing load.
- The supported target is Jellyfin 12/.NET 10, with real-server tests on 12.2. Future incompatible ABIs and arbitrary third-party SyncPlay replacements are not claimed supported.
- Native upgrade tests install an older package, expose a two-version catalog plus an incompatible-ABI fixture, run Jellyfin's automatic plugin update task, and verify version selection, restart activation, preserved policy settings and invalidated guest credentials. CI targets 12.0 and dynamically discovers the latest official stable 12.x; no future release is assumed compatible without passing tests.
- Physical GPU execution/HDR tone mapping, reverse-proxy forwarded-header trust/TLS behavior, and the administrator's chosen skins still require deployment-specific verification.
