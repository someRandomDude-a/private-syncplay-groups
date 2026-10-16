# Private SyncPlay for Jellyfin 12

Native .NET 10 plugin with private SyncPlay groups, limited invite links, administrator-controlled guest viewing, and bundled Jellyfin Web controls.

**Preview software.** Builds against Jellyfin 12.0's plugin ABI. Tested against a disposable Jellyfin **12.2 Docker server**, including real guest HLS generation/delivery, private API guards, guest API isolation, admission limits and permission revocation. The web launcher, private creation and invite-link creation have also been exercised in a browser. Complete the deployment checklist below for your own configuration before Internet exposure. Guest sharing is **off by default**.

## Features

- Public groups remain browsable through `/SyncPlay/List`. Private groups never appear there, even for their members.
- Private groups have a cryptographically random, case-insensitive eight-character join code. A known group ID alone does not authorize a private join or lookup.
- Server-wide and per-user defaults and creation permissions. Existing clients that omit a mode receive their effective default.
- Hosts create revocable invite links with guest permission, redemption count, and optional expiration. Links carry independent 256-bit secrets, not short join codes.
- Party-wide concurrent viewer limits count all signed-in sessions (including the host) and guest sessions, regardless of join method. Invite-use limits count successful new admissions; refreshing an existing lease does not consume another use.
- Administrator master switch and per-user Inherit/Allow/Deny for guest invitations. Server disable always wins. Revocation affects active guest leases as well as new admissions.
- **Guests never keep a party alive.** The last signed-in participant leaving or disconnecting destroys all party invitations and guest leases. Another signed-in member may keep the party alive after its creator leaves.
- State is in memory: restarting Jellyfin expires groups, links and guests. Disabling private creation does not publish existing private groups.

## Build and install

Requires the **.NET 10 SDK**, Node.js 22+, and npm. No other Jellyfin plugins are required.

```powershell
npm ci --ignore-scripts
npm run check
dotnet test tests/Jellyfin.Plugin.PrivateSyncPlay.Tests -c Release
./scripts/package.ps1
```

Extract `artifacts/PrivateSyncPlay-0.1.0.zip` into a new `PrivateSyncPlay` subdirectory inside Jellyfin's plugins directory, then restart Jellyfin. Do not copy the SDK or Jellyfin assemblies into that directory. The package embeds its web scripts and hls.js, whose license is included.

Configure **Dashboard → Plugins → Private SyncPlay**. Use the **Watch parties** launcher in Jellyfin Web to create a party, select its mode and concurrent-viewer cap, join by code, and manage invitations. The usual SyncPlay controls continue to handle signed-in playback.

Guest links must be created and opened over **HTTPS**. The web controls disable guest-link creation on an HTTP connection; account-only invitations remain available.

`scripts/package.ps1` also generates a repository manifest using the ZIP's actual checksum. Its release URL is not usable until you publish the corresponding GitHub release asset and host the manifest. Nothing is published automatically.

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

Each user can inherit or restrict public/private creation, override the default creation mode, and inherit/allow/deny guest invitations. A per-user Allow cannot bypass a server master restriction. Hosts cannot edit these policies.

Only the party owner can issue or revoke links. Revoking a link disconnects guests admitted by that link. Existing signed-in participants keep their normal membership. An admin can end a party through the API. Limits describe **sessions**, not verified unique humans; anonymous identity and physical viewers cannot be proven.

## Guest security model

An invite opens a minimal player at `/PrivateSyncPlay/Watch#<secret>`. Redemption requires an explicit click, HTTPS, a same-origin JSON POST, and a valid guest-enabled invitation. The fragment is immediately removed from the address bar. It is never sent as a request URL or Referer. Put the origin behind a correctly configured HTTPS reverse proxy if necessary; do not weaken the HTTPS checks.

Guests receive an opaque HttpOnly, Secure, SameSite=Strict, path-scoped session cookie, **not a Jellyfin user, account token or API key**. Secrets are stored as hashes server-side. These credentials are not registered with Jellyfin's normal authentication system. They cannot authorize ordinary Jellyfin APIs.

Guest endpoints expose only current playback state and a generated, sliding-window HLS stream. They never accept a source URL, library item ID, media path, or FFmpeg options from the guest. Every manifest and segment request checks the party, guest lease, owner sharing permission, current item, and owner library access. The player exposes local volume but no party controls; guests cannot send buffering/pause/seek/queue commands to SyncPlay.

Guest playback uses **one shared HLS process per active guest party**, through Jellyfin's configured FFmpeg executable. It follows an authenticated participant's reported playback position; it does not add guest sessions to Jellyfin's core SyncPlay engine or pause members while guests buffer. Expect browser/network synchronization tolerance, not frame-accurate playback. Administrator guest permission is an explicit delegation to stream the party's current video to unauthenticated people. The owner must retain media playback and SyncPlay permissions, plus remuxing/audio/video conversion permissions **only for the operations actually required**.

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
- Do not combine with plugins replacing `ISyncPlayManager`. Registration and startup checks fail rather than silently downgrade privacy.
- One guest lease per browser cookie jar; reopening it resumes the same session during the grace period. Losing the cookie requires a new redemption.
- Signed-in media restrictions remain enforced by Jellyfin. Enabling guest invitations is a deliberate admin decision to allow unauthenticated viewing of owner-authorized content.

## API

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
