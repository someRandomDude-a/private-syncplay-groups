# Private SyncPlay for Jellyfin 12

Native .NET 10 plugin with private SyncPlay groups, limited invite links, administrator-controlled guest viewing, and bundled Jellyfin Web controls.

**Preview software.** Builds against Jellyfin 12.0's plugin ABI. Tested against a disposable Jellyfin **12.2 Docker server**, including real guest HLS generation/delivery, private API guards, guest API isolation, admission limits and permission revocation. The web launcher, private creation and invite-link creation have also been exercised in a browser. Complete the deployment checklist below for your own configuration before Internet exposure. Guest sharing is **off by default**.

**Supported target: Jellyfin 12.x / .NET 10.** The latest official stable release checked on 2026-10-17 is **12.2**. CI discovers the latest stable **12.x** release from Jellyfin's official release API and tests both it and the minimum **12.0**, including installation and upgrade. Weekly checks catch new 12.x releases; they do not silently switch to 13.x. Other server majors are explicitly disabled by the plugin. A future release is not claimed tested until CI passes; hardware/proxy-specific checks still apply.

## Features

- Public groups remain browsable through `/SyncPlay/List`. Private groups never appear there, even for their members.
- Private groups have a cryptographically random, case-insensitive eight-character join code. A known group ID alone does not authorize a private join or lookup.
- Server-wide and per-user defaults and creation permissions. Existing clients that omit a mode receive their effective default.
- Hosts create revocable invite links with guest permission, redemption count, and optional expiration. Links carry independent 256-bit secrets, not short join codes.
- Party-wide concurrent viewer limits count all signed-in sessions (including the host) and guest sessions, regardless of join method. Invite-use limits count successful new admissions; refreshing an existing lease does not consume another use.
- Administrator master switch and per-user Inherit/Allow/Deny for guest invitations. Server disable always wins. Revocation affects active guest leases as well as new admissions.
- **Guests never keep a party alive.** The last signed-in participant leaving or disconnecting destroys all party invitations and guest leases. Another signed-in member may keep the party alive after its creator leaves.
- State is in memory: restarting Jellyfin expires groups, links and guests. Disabling private creation does not publish existing private groups.

## Install from Jellyfin's plugin catalog

Once the first tagged release has been published, add this stable repository URL:

```text
https://raw.githubusercontent.com/someRandomDude-a/private-syncplay-groups/catalog/manifest.json
```

1. Sign in as an administrator. Open **Dashboard → Plugins → Repositories** (or **Manage Repositories**).
2. Choose **+**, enter **Private SyncPlay** as the repository name and the complete URL above as its URL, enable it, then **Save**.
3. Open/refresh the **Catalog**, select **Private SyncPlay** in **General**, choose the compatible Jellyfin 12 version, and **Install**.
4. **Restart Jellyfin**, then configure **Dashboard → Plugins → Private SyncPlay**.

This follows Jellyfin's [third-party repository method](https://jellyfin.org/docs/general/server/plugins/#repositories): the repository URL serves a JSON array of plugin packages, not a GitHub page or a ZIP. Each version points to its immutable release ZIP and the MD5 checksum Jellyfin requires. The `catalog` branch preserves previous published versions, independently of GitHub's latest-release selection. GitHub/raw content and release assets must be publicly accessible to the server. **The URL returns 404 until the first release and catalog publication succeed; nothing has been published by this coding session.**

Do not add the SDK ZIP, a GitHub `/blob/…` page, the source metadata file, or a local filesystem path as a repository. If no plugin appears, check that the URL returns JSON with a nonempty `versions` array, the repository is enabled, the server is Jellyfin 12, and its network can reach GitHub. Jellyfin logs manifest/download/checksum failures. This catalog is not compatible with Jellyfin 10.x.

For manual installation, download **`PrivateSyncPlay-X.Y.Z.zip`** from [GitHub Releases](https://github.com/someRandomDude-a/private-syncplay-groups/releases), extract it into a `PrivateSyncPlay` folder inside Jellyfin's plugins directory, and restart. Keep **both** `Jellyfin.Plugin.PrivateSyncPlay.dll` and `Jellyfin.Plugin.PrivateSyncPlay.Api.dll` together. The separate `PrivateSyncPlay.Api-1.0.0.zip` asset is a developer SDK, not a Jellyfin catalog plugin.

### Version history and updates

- **Dashboard → Plugins → Catalog → Private SyncPlay** uses Jellyfin's normal **version selector**. It lists published compatible versions, newest first, with their changelogs/timestamps. An ABI newer than your server is filtered out. Before the first publication there are no installable versions; after the first publication there is only one until another is released. Test fixture versions are never published.
- The installed version appears in Jellyfin's **Plugins / My Plugins** list. The plugin settings page also displays the **running plugin version and server version**, so a downloaded update cannot be mistaken for code already running.
- With the repository enabled and Jellyfin's plugin automatic updates allowed, its **Update Plugins** scheduled task downloads newer compatible versions. You can also select a specific version and install it from the catalog. **Restart Jellyfin** to activate the new DLLs, then refresh Jellyfin Web. Updates are not hot-loaded into an existing browser page.
- Server/per-user plugin configuration persists across upgrades. Parties, invitations and guest cookies are deliberately invalidated on restart; hosts must create new parties/links. No temporary guest is turned into an account during an upgrade.
- Back up Jellyfin's data/configuration before upgrading. For manual updates, stop Jellyfin and replace the DLLs together in the existing manual-install directory; do not mix plugin and API DLLs from different packages or create duplicate unversioned copies. Prefer catalog installation for native version selection and update tracking.

## Build from source

Requires the **.NET 10 SDK**, Node.js 22+, and npm. No other Jellyfin plugins are required.

```powershell
npm ci --ignore-scripts
npm run check
npm run test:ui
dotnet test tests/Jellyfin.Plugin.PrivateSyncPlay.Tests -c Release
./scripts/package.ps1
```

Extract `artifacts/PrivateSyncPlay-0.1.0.zip` into a new `PrivateSyncPlay` subdirectory inside Jellyfin's plugins directory, keeping both packaged DLLs together, then restart Jellyfin. Do not install the separate developer SDK archive as a plugin or copy Jellyfin server assemblies into that directory. The package embeds its web scripts and hls.js, whose license is included.

Configure **Dashboard → Plugins → Private SyncPlay**. Use the **Watch parties** toolbar button in Jellyfin Web to create a party, select its mode and concurrent-viewer cap, join by code, and manage invitations. The usual SyncPlay controls continue to handle signed-in playback.

Guest links must be created and opened over **HTTPS**. The web controls disable guest-link creation on an HTTP connection; account-only invitations remain available.

`scripts/package.ps1` generates the installable `artifacts/manifest.json` using the exact ZIP's checksum and also packages the developer SDK. `scripts/plugin-metadata.json` is **build input only**, not a repository; there is deliberately no empty root manifest pretending to be installable. Use the published raw `catalog/manifest.json` URL above in Jellyfin.

The packaging contract is checked against Jellyfin 12.2's [installer](https://github.com/jellyfin/jellyfin/blob/v12.2/Emby.Server.Implementations/Updates/InstallationManager.cs): four-component plugin/ABI versions, matching plugin GUID, direct `.zip` URL, MD5 of the exact ZIP, and plugin DLLs at the archive root. Jellyfin creates the versioned plugin directory and local `meta.json` during installation; the repository manifest is **not** copied into the plugins folder. MD5 is Jellyfin's integrity check, not a digital signature—only add repositories you trust.

### Publishing a release (maintainers)

Commit the source changes, create a version tag such as `v0.1.0`, and push the tag. `.github/workflows/release.yml` verifies .NET/UI tests and installs the packaged ZIP through a real Jellyfin Docker server's repository API, then tests the plugin and a separate API consumer. It publishes the plugin ZIP, SDK ZIP and manifest to that GitHub release **before** updating `manifest.json` on the dedicated `catalog` branch. No GitHub Pages setup is needed. The workflow needs `contents: write` permission and permission to create/update that branch. Existing release assets and catalog versions are not overwritten. Alternatively, run the release workflow manually with an **existing** `vX.Y.Z` tag.

The verification job builds the **tag's exact version**, tests that ZIP, and passes the verified files to the publication job. Publication merges historical catalog entries with `scripts/merge-catalog.ps1` **without rebuilding or changing the tested ZIP**. Numeric version ordering, unchanged historical checksums/URLs, duplicate rejection and increasing release versions are regression-tested. The SDK keeps its own API version, independently of plugin release versions.

The workflow merges the previous published catalog; local packaging can do the same with `./scripts/package.ps1 -Version X.Y.Z -PreviousManifest path/to/previous-manifest.json`. Reusing a version already in that catalog is rejected. Local packaging alone never creates a GitHub release or updates the catalog branch. If release creation succeeds but catalog publication fails (for example, branch protection), fix the permission and publish the **existing release's manifest**, not a rebuilt ZIP, with `scripts/publish-catalog.ps1`; supply the current catalog commit as `-ParentCommit` when the branch exists. The old catalog remains usable until the new one is published.

### Native UI and skin compatibility

Controls use Jellyfin 12's native DOM/CSS contracts: `emby-button`, `raised`, `button-submit`, `button-delete`, `inputContainer`/`emby-input`/`inputLabel`, `checkboxContainer`/`emby-checkbox`/`checkboxOutline`, `selectContainer`/`emby-select`, `dialog`/`formDialog`, `paperList`/`listItem`, and `mdl-slider`. Labels are associated with inputs, focus states follow native classes, and errors use themed in-page feedback instead of browser alerts.

The launcher is placed in Jellyfin's toolbar and reuses its actual button classes, including the Modern layout's MUI skin hooks. There is no fixed-position floating button. Plugin CSS provides responsive layout and inherited defaults only, not a custom palette, font or button design. Existing themes, CSS variables and skin overrides own the appearance.

The isolated guest player loads **stylesheets only** from the installed Jellyfin Web build, its configured default theme and public server branding CSS. It never loads the normal web application or its authentication code. Guests cannot inherit another user's private client preferences; server-wide skins apply through **Dashboard → General → Custom CSS**. HTTPS stylesheet/font/image imports are permitted for administrator-installed skins; scripts, media access and API requests remain restricted. Only install trusted CSS, and never put account tokens or private media URLs in public branding CSS.

Native skin selectors are supported, but arbitrary CSS that hides controls or completely replaces Jellyfin's DOM can still break them. Skins targeting only unrelated MUI components may need the same legacy control selectors used by ordinary Jellyfin plugin configuration pages. Test your chosen skin in Modern/Legacy layouts and at mobile sizes.

## Administrator settings

| Setting | Default | Behavior |
| --- | --- | --- |
| Enable private groups | On | Permits creation; existing private groups remain protected if disabled |
| Allow public groups | On | Permits public creation |
| Default creation mode | Public | Per-user override; must select an allowed mode |
| Enable guest invites | **Off** | Master guest-sharing permission |
| Default guest permission | **Off** | Inherited permission when master switch is enabled |
| Maximum simultaneous viewers | 10 | Hard upper bound on each party's chosen cap |
| Maximum invite uses | 10 | Hard upper bound on each link's chosen redemption count |
| Guest reconnect grace | 30 seconds | Seats expire without a state heartbeat |
| Guest video bitrate | 2000 kbps | Shared browser-stream video bitrate cap; audio target up to 128 kbps |
| Maximum concurrent guest streams | 2 | Server-wide encoder/remux process limit (1–32); additional parties receive 503, not another encoder |

Each user can inherit or restrict public/private creation, override the default creation mode, and inherit/allow/deny guest invitations. A per-user Allow cannot bypass a server master restriction. Hosts cannot edit these policies.

Only the party owner can issue or revoke links. Revoking a link disconnects guests admitted by that link. Existing signed-in participants keep their normal membership. An admin can end a party through the API. Limits describe **sessions**, not verified unique humans; anonymous identity and physical viewers cannot be proven.

## Guest security model

An invite opens a minimal player at `/PrivateSyncPlay/Watch#<secret>`. Redemption requires an explicit click, HTTPS, a same-origin JSON POST, and a valid guest-enabled invitation. The fragment is immediately removed from the address bar. It is never sent as a request URL or Referer. Put the origin behind a correctly configured HTTPS reverse proxy if necessary; do not weaken the HTTPS checks.

Guests receive an opaque HttpOnly, Secure, SameSite=Strict, path-scoped session cookie, **not a Jellyfin user, account token or API key**. Secrets are stored as hashes server-side. These credentials are not registered with Jellyfin's normal authentication system. They cannot authorize ordinary Jellyfin APIs.

Guest endpoints expose only current playback state and a generated, sliding-window HLS stream. They never accept a source URL, library item ID, media path, or FFmpeg options from the guest. Every manifest and segment request checks the party, guest lease, owner sharing permission, current item, and owner library access. The player exposes local volume but no party controls; guests cannot send buffering/pause/seek/queue commands to SyncPlay.

Guest playback uses **one shared HLS process per active guest party**, through Jellyfin's configured FFmpeg executable. It follows an authenticated participant's reported playback position; it does not add guest sessions to Jellyfin's core SyncPlay engine or pause members while guests buffer. Expect browser/network synchronization tolerance, not frame-accurate playback. Administrator guest permission is an explicit delegation to stream the party's current video to unauthenticated people. The owner must retain media playback and SyncPlay permissions, plus remuxing/audio/video conversion permissions **only for the operations actually required**.

### Failure containment

- Unsupported/conflicting SyncPlay registrations **disable this plugin**, log an administrator-facing error, and leave the existing server engine untouched. Plugin endpoints return **503** rather than creating unprotected parties. A later safety-check or maintenance failure revokes all plugin invitations/guest leases and latches the plugin unavailable until restart; it does not deliberately stop Jellyfin's host.
- Revocation happens **before** native group/encoder cleanup. Failed cleanup cannot preserve a usable guest credential. If encoder termination cannot be confirmed, further plugin access is disabled rather than repeatedly spawning replacement processes. An administrator may need to stop an orphaned encoder at the OS/container level.
- Encoder launch failures return generic errors without server paths and have a retry backoff. No failure switches guests to normal Jellyfin media URLs, issues account tokens, removes permission checks, or starts a hidden software fallback.
- Guest polling/media requests are rate-limited; request bodies and media allocations are bounded. Guest processes have a separate server-wide cap (default **2**), including remux jobs; this does not replace OS resource limits or Jellyfin's signed-in transcoding controls.
- Every guest delivery rechecks an eligible, connected signed-in member and the owner's permission. Cross-origin requests, including sibling-subdomain requests carrying a SameSite cookie, are rejected when browser origin/fetch metadata is present. Non-browser callers must still present the unguessable guest credential.

See [security review and regression coverage](docs/security-review.md). These safeguards are not a guarantee against all crashes: plugins and FFmpeg run with the server's privileges. Keep Jellyfin/FFmpeg patched, use trusted plugins and media, run Jellyfin as an unprivileged user/container with read-only media and appropriate CPU/memory limits, and verify proxy behavior before Internet exposure. Jellyfin's existing public login, branding and server-info endpoints remain public even without this plugin.

### Encoding policy: follow Jellyfin

The implementation follows Jellyfin's [transcoding guidance](https://jellyfin.org/docs/general/post-install/transcoding/) and [hardware acceleration configuration](https://jellyfin.org/docs/general/post-install/transcoding/hardware-acceleration/):

- Compatible H.264/AAC media within the bitrate cap is **stream-copied/remuxed**; neither video nor audio is re-encoded. The guest transport intentionally remains gated HLS instead of exposing the original file.
- If only the audio is incompatible, copy the video and convert only the audio (Direct Stream).
- If video conversion is required by codec, bit depth, SDR browser profile or bitrate, use Jellyfin's native `EncodingHelper` and current `EncodingOptions`. These select the configured HWA backend (QSV, NVENC, VA-API, AMF, VideoToolbox, RKMPP), decoder/device arguments, filter chain, tone-mapping options, encoder preset/quality, audio downmix and thread count. The plugin does not maintain its own GPU preference or hard-coded GPU arguments.
- Software video encoding is used only when conversion is necessary and Jellyfin selects it—for example, HWA is disabled, hardware encoding is unchecked, or the configured encoder is unavailable. A failed hardware process does **not** trigger a hidden plugin-side CPU retry.
- Encoding setting changes replace the shared guest stream on the next state update. The former fixed 1280px scaler and two-thread/software-encoder override have been removed; sizing follows Jellyfin's bitrate-driven rules.

The shared guest profile remains conservative H.264 8-bit SDR plus AAC stereo, rather than negotiating different codecs per guest. Supported source streams are copied when compatible with that profile. Configure hardware acceleration under Jellyfin's **Dashboard → Playback → Transcoding**, including device access for Docker, as explained in the linked documentation.

Ending a party or revoking access prevents further delivery and stops unused encoders. Bytes already delivered or buffered cannot be recalled or made unrecordable. Guests necessarily know the public streaming address, and Jellyfin's existing public login/server-info endpoints remain public; the plugin grants no additional access to them. It is not a network firewall.

### Initial limitations

- Server-hosted Jellyfin Web only. Native clients remain subject to privacy/limits but do not gain private-code or guest controls.
- Guest playback supports **local video files only**, using the first video/audio tracks. No guest subtitles, Live TV, remote/STRM sources or disc images in this preview.
- Requires Jellyfin's FFmpeg build with the selected encoders, HLS and `readrate_initial_burst`. Remuxing has low overhead; required guest conversion is additional load, separate from signed-in transcoding.
- Hardware encoder selection is covered by tests using Jellyfin's actual helpers and mocked hardware capabilities. Actual GPU execution and HDR tone mapping must still be verified on the administrator's hardware; Docker integration tests exercise real stream-copy and required software conversion on a CPU-only server.
- Do not combine with plugins replacing `ISyncPlayManager`. Registration/startup checks disable this plugin rather than silently downgrade privacy or intentionally abort server startup.
- One guest lease per browser cookie jar; reopening it resumes the same session during the grace period. Losing the cookie requires a new redemption.
- Signed-in media restrictions remain enforced by Jellyfin. Enabling guest invitations is a deliberate admin decision to allow unauthenticated viewing of owner-authorized content.

## API

### Internal API for other Jellyfin plugins

Reference the versioned SDK and register `services.AddPrivateSyncPlayClient()` inside your `IPluginServiceRegistrator`. Then constructor-inject `IWatchPartyService` to create/query parties, generate invitation links on behalf of authenticated users, join, revoke, leave or end parties without HTTP calls. All operations retain user/session checks, native SyncPlay permissions, administrator guest restrictions, capacity limits and party lifetime rules.

See **[plugin API documentation and complete examples](docs/plugin-api.md)**, including typed method signatures, XML/IntelliSense documentation, optional dependency handling and Jellyfin's isolated assembly load contexts. [`examples/WatchPartyConsumer`](examples/WatchPartyConsumer) is a separately compiled example plugin used in Docker integration tests; it is not shipped in the production plugin ZIP.

### HTTP API

Authenticated routes retain Jellyfin's SyncPlay authorization policies and resolve the caller's existing device session:

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/PrivateSyncPlay/Me` | Effective permissions and own current party; code only for its owner |
| POST | `/PrivateSyncPlay/Groups` | `{ Name, Mode: 0(public)/1(private), MaxViewers }` |
| POST | `/PrivateSyncPlay/JoinCode` | `{ Code }` |
| POST | `/PrivateSyncPlay/JoinInvite` | `{ Token }`; still requires the user's own media access |
| POST | `/PrivateSyncPlay/Invites` | `{ AllowGuests, MaxUses, ExpiresAt? }`; owner only |
| DELETE | `/PrivateSyncPlay/Invites/{id}` | Owner revocation |
| DELETE | `/PrivateSyncPlay/Groups/{id}` | Admin-only party termination |

Anonymous guest routes are deliberately separate: `POST Guest/Redeem`, `GET Guest/State`, `GET Guest/media/{generation}/{name}`, `POST Guest/Leave`, under `/PrivateSyncPlay/`. There is no guest playback-control endpoint. Reverse proxies must not cache these responses or log request bodies/cookies.

## Integration checklist before deployment

To run the automated disposable-server test yourself, with Docker running:

```powershell
dotnet build src/Jellyfin.Plugin.PrivateSyncPlay -c Release
node scripts/integration.mjs
```

To test the actual repository/ZIP installation path (also run in CI): build `examples/WatchPartyConsumer`, run `scripts/package.ps1`, then `node scripts/integration.mjs --with-consumer --install-from-catalog`. This starts a temporary nginx repository on a dedicated Docker network, installs through Jellyfin's `Repositories`/`Packages` APIs, verifies the DLLs and generated `meta.json` in its versioned plugin folder, restarts Jellyfin, and runs the playback/security tests. The test repository changes only the package's URL to its Docker-local fixture; its ZIP bytes and MD5 remain those of the generated release package. No public release is required for this local test.

For the native update/version-history test, run `./scripts/test-upgrade.ps1 -Directory <new-temporary-directory> -Image jellyfin/jellyfin:12.2` after building the example consumer. It builds fixture versions 0.0.1/0.0.2 only in that temporary directory, installs the older version, publishes the newer fixture, runs Jellyfin's Update Plugins task and verifies restart activation, retained configuration, compatible version listing and invalid guest credentials. It then runs the full playback/security suite against the updated plugin. Use `node scripts/jellyfin-versions.mjs` to discover the current supported CI image matrix. Fixture builds change local `bin` output; rerun normal packaging afterward to restore the preview build.

It uses the official `jellyfin/jellyfin:12.2` image, creates only new test data under the temporary directory, exposes randomly assigned **loopback-only** ports, generates a synthetic video and short-lived self-signed certificate, and removes its own container afterward. It does not mount or modify existing server data. The self-signed certificate exception applies only to the script's test HTTP client. Test data/diagnostics remain in the printed temporary folder. CI runs both this test and the unit tests.

Use a disposable Jellyfin 12 instance, HTTPS, two test accounts and a short local video:

1. Install/restart, load both Modern and Legacy web layouts, and verify the launcher and dashboard settings.
2. Create private/public groups. Confirm `/SyncPlay/List` contains only public groups; outsider ID-based join and detail lookup fail. Check native/public joins also obey capacity.
3. Set a per-user private default and denied creation mode. Test both the custom and stock create endpoints. Verify no policy bypass with manually constructed requests.
4. Enable guest sharing for only one owner. Open a guest-enabled link in a private browser. Inspect requests: no Jellyfin token, original media URL, library response, server path or unrelated metadata should appear.
5. Test simultaneous redemptions at the capacity/use limits, lease expiration/reconnect, account-only links, invite expiration/revocation, and admin Allow→Deny→Allow. Old revoked guest credentials must remain invalid.
6. Test host play/pause/seek, track changes and network recovery. Attempt arbitrary segment names, other media generations, and normal Jellyfin APIs using only the guest cookie.
7. Remove the host while another signed-in member remains; guests may continue. Remove the final signed-in member; all guest requests must fail, invitations must expire and the encoder must stop.
8. Disable sharing, remove owner media or required conversion permissions, disable/delete the owner, and restart the server. Verify further guest delivery is denied. Verify that a remux-only stream does not require video transcoding permission. Review FFmpeg load and cache cleanup.
9. Use compatible and incompatible sources at different bitrate caps. Confirm remux/direct stream before video conversion; then configure your HWA backend and verify the generated FFmpeg process uses it. Check your preset, thread count, device permissions and tone-mapping settings.

Automated tests exercise admission limits, private ID/list guards, invitation and lease lifetimes, permission inheritance/revocation, concurrent redemption, origin/HTTPS checks, route separation and asset allowlisting. They do not substitute for the real-server checklist.
