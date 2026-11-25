# ADR 0001: Client architecture (TypeScript UI + Rust core in Tauri); keep the .NET server

- **Status:** Accepted, 2026-11-25. To be validated by the client spike; see the roadmap (#45).
- **Supersedes:** the Avalonia client (`src/Snacka.Client`), the SIPSorcery media stack, the custom SFU (`Snacka.Server/Services/Sfu`) and coturn.

## Context

Snacka is for friends talking and playing games together; one of them hosts the server. It's self-hosted at friend-group scale, so **quality of service and stability matter more than scale**.

Requirements:
- **Windows, Linux and macOS:** chat, voice, webcam, screen share, and game streaming as both **host and viewer**, at the highest achievable quality (up to 4K120) with input lag as close to local play as possible.
- **Android:** client only (chat, voice, viewing streams).
- **Web:** a browser client.
- Maintained by **one developer** (with AI assistance).

Findings from the November 2026 review:
- **The media stack is where the risk and the work are.** That's SIPSorcery (two high-severity advisories), a custom SFU (with a suspected SSRC mix-up for 3+ participants), FFmpeg subprocesses, hand-wired echo cancellation, and six native helpers in Swift and C++. Most past bugs were platform-specific media crashes.
- SIPSorcery was chosen for being "pure C#, no native dependencies". That reason no longer holds.
- **None of the current client media stack runs on Android.** The Avalonia client has no realistic path to a web client with media.
- **Measured on macOS** (`spikes/stream-probe`, M4 Pro): a hardware-encoded custom pipeline reaches 6–9 ms from capture to decoded frame up to 1440p120. 4K is limited by the encoder (~17 ms per frame on this chip).
- **The .NET server is the most solid part:** it has an integration test base and most of the tests, and its issues are specific and small.

## Decision

1. **One UI codebase in TypeScript** (React + Vite). It runs as:
   - the **web client** in any browser,
   - the **desktop app** inside **Tauri 2** on Windows, macOS and Linux,
   - the **Android app** inside **Tauri 2 mobile**.
2. **A Rust core** (`snacka-core`), used by the desktop and Android apps and owning everything latency-sensitive or OS-specific:
   - **Voice, webcam and screen share** through **LiveKit** (libwebrtc via the LiveKit Rust SDK), which gives echo cancellation, noise suppression, jitter buffering and congestion control.
   - **The game-streaming engine:** capture → hardware encode (NVENC, AMF, QSV, VideoToolbox, VAAPI) → peer-to-peer low-latency transport → hardware decode → **native presentation surface**. Game-stream frames never pass through the webview. Design is in #49.
   - Controller and input capture and injection (virtual gamepads).
3. **The browser client** uses the **LiveKit JS SDK** for voice, webcam and watching streams. Hosting game streams is desktop-only.
4. **Keep the ASP.NET Core server** for accounts, communities, chat, DMs, files and presence:
   - It issues LiveKit tokens per voice channel.
   - It helps game-streaming peers find each other.
   - The TypeScript client uses the official SignalR JS client and a TypeScript API client generated from OpenAPI.
   - The custom SFU and coturn are removed; LiveKit server provides the SFU and TURN.
5. **Make hosting one click:** a single self-contained server download (server + LiveKit binary, SQLite, no Docker required), and a "Host a server" option in the desktop app that runs it as a Tauri sidecar.

## Alternatives considered

| Option | Why not |
|---|---|
| Keep Avalonia (C#) and add a Rust media core | It would mean two UI codebases (Avalonia plus a separate web client), and most of the C# client's value is the media code being replaced anyway. |
| Rust + GPUI (Discord's 2026 direction) | Best desktop footprint, but no Android support and only experimental web support, so three UI codebases. Discord can afford that; one developer can't. |
| Electron | Consistent Chromium WebRTC on every OS, but heavy and no Android support. Our media runs in the Rust core, so we don't need the webview's WebRTC on desktop. |
| Flutter + Rust | One UI codebase, but its web output (canvas-rendered) is weaker for a chat app, and a TypeScript UI has a larger ecosystem for this kind of app. |
| Rewrite the server too | Users wouldn't notice; it would throw away the best-tested code. Revisit only if a concrete reason appears. |

## Consequences

- **Languages:** TypeScript (UI), Rust (client core), C# (server). The native per-OS code moves into Rust (`windows`, `objc2`/ScreenCaptureKit, PipeWire crates) instead of separate Swift and C++ helper processes.
- **Development continues in a new `client/` directory**, while the old `src/Snacka.Client` keeps working until the new client reaches parity; the old one is then deleted.
- **Issues that only concern the Avalonia client or the custom SFU are closed as superseded.** Server issues stay.
- **Risks the spike must answer first:**
  1. Embedding a native low-latency video surface in a Tauri window on all three OSes.
  2. Tauri on Linux (WebKitGTK quirks).
  3. How mature Tauri's Android support is.
  4. The LiveKit Rust SDK's hardware encoding on Windows (#46).

## Spike results (2026-11-25)

The spike (#51, #49) answered the first three risks on real hardware. **The decision stands.**

| Risk | Result |
|---|---|
| 1. Native video surface in the Tauri window | **Works on all three desktop OSes.** macOS: a layer-backed NSView above the WKWebView showing VideoToolbox IOSurfaces. Windows: a DXGI flip-model child window with Media Foundation D3D11 decode (#57). Linux: a Wayland subsurface with zero-copy VAAPI DMA-BUFs (#58; X11 still to do, #65). |
| 2. Tauri on Linux (WebKitGTK) | **Good enough to ship.** No hitches, 30–40 ms mount, 17 ms typing→frame, near-zero idle CPU, on Wayland and XWayland. Like WKWebView on macOS, it renders the UI at 60 fps on high-refresh displays (#64). That only affects UI smoothness: game streams bypass the webview. |
| 3. Tauri on Android | **Works:** the same UI with login and realtime chat in the emulator. Mobile layout and MediaCodec video remain (#47). |
| 4. LiveKit Rust SDK hardware encoding on Windows | Not tested yet (#46). Voice works through the LiveKit JS SDK in the browser (#52). |

Game-streaming loopback (synthetic GPU frames → hardware encode → UDP wire format → hardware decode), capture → decoded p50:

| Machine | 1440p120 | 4K120 |
|---|---|---|
| Windows, RTX 4090 (NVENC, HEVC) | 3.9 ms | **4.0 ms** |
| Linux, Radeon 8060S iGPU (VAAPI, HEVC) | 5.5–8 ms | 12–13.5 ms |
| macOS, M4 Pro (VideoToolbox, HEVC) | 6–9 ms | not reachable (~17 ms encode per frame) |

Capture → on screen in the Tauri window: about 10 ms on macOS (1440p120) and 4.4–4.7 ms on Windows (4K120). On Linux it's 19–23 ms at 1440p120, measured end to end with `wp_presentation`; #65 tracks reducing it.

The full reports are in `spikes/stream-probe/results-m4pro.md`, `client/results/windows-rtx4090.md` and `client/results/linux-radeon-8060s.md`.

**Consequences for the engine (#49):**
- **Pick the codec per GPU:** HEVC by default; AV1 where the encoder is reliable (it isn't on AMD/Mesa today, #63).
- **Adaptive quality is required.** Laptops in power-saving modes and integrated GPUs at 4K120 sit right at the frame budget.
