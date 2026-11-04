# Venue Audio — in-game internet test build

A standalone Dalamud dev plugin. No npm, console, installer, or game restart is needed.

## Install and update in game

Add this custom plugin repository under **Dalamud Settings → Experimental → Custom Plugin Repositories**:

`https://raw.githubusercontent.com/NoctusDev/buddys-buddy/main/repo.json`

Then open **Plugin Installer → All Plugins**, search **Venue Audio**, and install it. If you already use this repository for Buddy's Buddy, refresh the plugin list and search. Updates use Dalamud's normal plugin updater. No manual file download, console, or game restart is required.

If using the earlier development build, unload its Dev Tools entry before enabling the repository version so only one copy runs.

## Developer loading while FFXIV stays open

Use your existing **Dev Plugin Locations** list:

`D:\CLAUDE\Buddy's Buddy\venue-audio\native\VenueAudio.Plugin\bin\Release\VenueAudio.dll`

Add the full DLL path under **Dalamud Settings → Experimental → Dev Plugin Locations**, save, then **Plugin Installer → Dev Tools → Scan Dev Plugins → Load Venue Audio**. For an already loaded older version, unload/reload this plugin or use its automatic reload setting. Keep the entire build folder, including **Tools**, together.

## Broadcast to friends on other networks

1. Choose **Stream Mix (Elgato Virtual Audio)** or another PC audio source.
2. Leave **Automatic internet hosting** enabled (the new default).
3. Optionally add a microphone if your source mix does not already include it.
4. Click **Start broadcasting**. Allow up to about a minute for a temporary public address.
5. Click **Copy guest invite** and send the complete invite to friends with this plugin.

Friends choose **Paste invite → Listen** in game. They do not need a hosting account or router changes. Listening never opens the microphone. Stop/unload closes audio capture and the host/tunnel processes. Guest volume is independent. The DJ uses a local connection; guests use the secure public tunnel.

Output-device capture includes all audio on that device, including game sounds. A mixer input lets you choose what is included. Application-only capture is not implemented. Playlists/transitions remain in your music or DJ software; the plugin transmits the resulting continuous audio.

Use Thunderdome's music-off setting if using its booth/effects. Penumbra and Lightless handle visuals as usual; song changes need no mod reload.

## Internet hosting and encryption

Automatic hosting starts the bundled self-contained relay helper and official Cloudflare tunnel helper in the background. No console windows or separate developer tools are required. A temporary public HTTPS/WebSocket address is generated for each broadcast. No Cloudflare account is required for this testing mode.

Audio is encrypted in the plugin using AES-256-GCM before entering the relay. The room key is in the guest invite and is not sent in the server URL or room handshake. The relay sees encrypted audio packets. Share the complete invite only with intended listeners. Transport additionally uses TLS on the public connection. This is a new implementation with automated checks, not an independently audited cryptographic product.

Quick Tunnels are a testing service with no uptime guarantee. The address changes each time. This is **relayed audio**, not native peer-to-peer transport, and should not be treated as a permanent large-venue hosting plan.

Advanced options retain local hosting and a separately hosted secure relay. A Render deployment blueprint and Dockerfile are prepared, but no permanent public service has been deployed. Render is not required for the temporary test mode.

## What has been tested

- Release plugin targets the installed **Dalamud API 15**.
- 39 native/local checks pass: authorization, invitations, encryption/tamper/replay checks, Opus encode/decode, fan-out, WebSocket gateway, Windows device enumeration, muted native playback, loopback capture, full sessions, cleanup and restart.
- The automatic public tunnel carried generated test audio through a real external secure endpoint. Public-path testing is from this PC, not an independent two-household listening test.
- This PC's configured DNS had difficulty resolving freshly generated tunnel names. The public diagnostic used published DNS records without changing Windows/VPN settings. Product guests use ordinary OS DNS. A fresh invite may need time before its hostname resolves.
- No real PC audio/microphone was transmitted during the public synthetic-audio tests.

Manual QA still needed: plugin load/unload inside the running game, an audible test with a remote friend, microphone mix levels, device removal, WAN jitter and larger audiences.

## Peer-to-peer direction

Buddy's Jobs currently uses a Cloudflare Worker and D1 for shared state, rather than P2P. Similar small room/signaling messages could help establish encrypted direct audio connections. The jobs service has not been modified.

A P2P transport would reduce central media traffic for direct connections, but each guest adds DJ upload traffic; some networks still need a TURN relay fallback. Native WebRTC/P2P has **not** been implemented in this build. The source/capture/encryption/playback components can be retained when adding it.

## Developer tools (not user steps)

`native/Build.ps1 -InternetTools` packages the background host and builds the plugin. `-Tests -Audio` runs native hardware/session checks; `-Tests -Internet` runs public synthetic-audio checks. Build caches stay in `.build-cache`.

`hosting/Dockerfile` and `render.yaml` prepare the permanent WebSocket relay for a free Render testing service. It requires a broadcaster access key and exposes `/health`; TLS is handled by the hosting provider. No keys or accounts are bundled.

References: [Cloudflare Quick Tunnels](https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/), [Dalamud hot reload](https://dalamud.dev/faq/getting-started/#how-do-i-hot-reload-my-plugin), [NAudio](https://github.com/naudio/NAudio/tree/v2.2.1), [Concentus](https://github.com/lostromb/concentus).
