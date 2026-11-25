# Handoff: Linux (WebKitGTK UI performance + hardware video pipeline)

Read `docs/handoff/README.md` first. You are on branch `handoff/linux-media`; push your work here.

**Why this matters:** Tauri on Linux renders the UI with **WebKitGTK**, which has a reputation for poor frame rates (see #51 comments). The owner explicitly wants this measured so Linux users aren't let down. Video is drawn natively, not by the webview, so WebKitGTK only affects UI smoothness. Linux is also a host and viewer platform for game streaming (VAAPI or NVENC).

## 0. Setup
- Tauri prerequisites (Debian/Ubuntu names): `libwebkit2gtk-4.1-dev build-essential curl wget file libxdo-dev libssl-dev libayatana-appindicator3-dev librsvg2-dev`; Rust (rustup); Node 22+ and pnpm.
- For video: `libva-dev vainfo` (Intel/AMD) or the NVIDIA driver with NVENC/NVDEC; `vainfo` / `nvidia-smi` output goes in the report.
- `cd client && pnpm install && pnpm --dir ui install`
- Record in the report: distro, kernel, desktop environment, **X11 or Wayland** (`echo $XDG_SESSION_TYPE`), GPU and driver, WebKitGTK version (`pkg-config --modversion webkit2gtk-4.1`), display refresh rate.

## 1. UI benchmark: the WebKitGTK question (do first)
- `pnpm tauri build --debug --no-bundle`, then `SNACKA_BENCH=1 SNACKA_BENCH_EXIT=1 ./target/debug/snacka-app | grep ^BENCH`
- Run it in **each session type available: Wayland and X11** (log out, pick the other session), and with these variants:
  - default
  - `WEBKIT_DISABLE_DMABUF_RENDERER=1` (known workaround for NVIDIA/Wayland issues)
  - `WEBKIT_DISABLE_COMPOSITING_MODE=1` (for comparison only)
- Chrome baseline: `pnpm --dir ui build && pnpm --dir ui preview`, open `http://localhost:4173/#bench` in Chrome/Chromium at 1280×800.
- Also check by hand: idle CPU of the app (`top`/`pidstat` for 60 s with a channel open), window resize smoothness, text rendering/HiDPI scaling, IME input.
- **Pass criteria** (same as #51): scroll ≥ 55 fps, ≤ 5 hitches over 25 ms, mount < 100 ms, typing→frame p95 < 34 ms.
- Commit a table to `client/results/linux-webkitgtk.md` and comment a summary on #51. **If it fails:** profile what's slow (WebKit Inspector: right-click → Inspect, or `WEBKIT_INSPECTOR_SERVER`), try cheap CSS fixes (`contain`, fewer layers), and file an issue describing the fallback options from #51 (Chromium-based shell on Linux only).

## 2. Hardware encode/decode in Rust
Port the Swift probe's synthetic source and barcode writer to Rust (`examples/send.rs`; barcode writer in `barcode.rs` with a round-trip test). Coordinate with the Windows handoff (#57): whichever lands first, the other reuses it.
- **Encoder:** VAAPI (Intel/AMD) or NVENC (NVIDIA). Pragmatic route: FFmpeg libav via `ffmpeg-next` with `h264_vaapi`/`hevc_vaapi` or `h264_nvenc`/`hevc_nvenc`, low-latency settings (no B-frames, ultra-low-latency tune / `-async_depth 1`, on-demand IDR). Upload frames as VAAPI/CUDA surfaces; no CPU path at 4K.
- **Decoder:** VAAPI or NVDEC to GPU surfaces, exported as **DMA-BUF** for zero-copy presentation. Same shape as `videotoolbox.rs` (`Decoder::decode(...) -> Option<DecodedFrame>`, `with_luma` for the barcode in test mode).
- Make `VideoReceiver` (`stream/mod.rs`) work on Linux.
- Produce the same matrix as `spikes/stream-probe/results-m4pro.md` in loopback and commit it as `client/results/linux-<gpu>.md`.

## 3. Native video surface in the Tauri window
`client/src-tauri/src/video_surface_linux.rs`, with the same commands as macOS (`start_test_stream`, `set_video_rect` with `viewport_height`). Options to evaluate, lowest latency first:
1. **Wayland:** a `wl_subsurface` of the toplevel in desync mode, showing DMA-BUF frames via `linux-dmabuf` (no GL copy). Positioned above the WebKitGTK widget.
2. **X11:** a child X window with EGL/GLX (or Vulkan) presenting the imported DMA-BUF.
3. **Simplest fallback:** a `gtk::Overlay` with a `GtkGLArea` above the webview (DMA-BUF → EGLImage → GL texture). Likely adds a compositor frame; measure it.

Verify alignment with screenshots (macOS needed a title-bar inset fix, commit `bfe86bd`), HiDPI scaling, resize and fullscreen. Report capture→present latency in loopback.

## 4. Cross-machine
Mac sender (`spikes/stream-probe ... send --host <linux-ip>`) → Linux receiver: fps, drops, keyframe recovery (join mid-stream; restart the sender).

## Done when
- [ ] WebKitGTK benchmark on Wayland **and** X11 (+ Chrome baseline) on #51, with a clear verdict: good enough, or needs the fallback
- [ ] Encode/decode latency matrix for this GPU on #49
- [ ] Native surface works in the Tauri window on Linux (screenshot in the PR), with the chosen approach documented
- [ ] Follow-up issues filed for anything not finished
