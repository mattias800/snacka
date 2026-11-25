# Handoff: Windows (WebView2 UI benchmark + hardware video pipeline)

Read `docs/handoff/README.md` first. You are on branch `handoff/windows-media`; push your work here.

**Why this matters:** Windows + NVIDIA is the main gaming host. On macOS the M4 Pro's encoder tops out at ~17 ms per 4K frame, so 4K120 hosting there isn't possible. We need to know whether NVENC on this PC reaches **4K120 at low latency**, and whether the Tauri UI (WebView2) is smooth.

## 0. Setup
- Rust (rustup, MSVC toolchain), Node 22+ and pnpm, Visual Studio Build Tools with "Desktop development with C++". WebView2 ships with Windows 11.
- `cd client && pnpm install && pnpm --dir ui install`
- Note the hardware in your report: CPU, GPU and driver version, display refresh rate.

## 1. UI benchmark (quick, do first)
- `pnpm tauri build --debug --no-bundle`, then `$env:SNACKA_BENCH=1; $env:SNACKA_BENCH_EXIT=1; .\target\debug\snacka-app.exe | Select-String BENCH`
- Chrome/Edge baseline: `pnpm --dir ui build; pnpm --dir ui preview`, open `http://localhost:4173/#bench` (1280×800 window).
- Compare with the macOS baselines in #51. Pass: scroll ≥ 55 fps, ≤ 5 hitches over 25 ms, mount < 100 ms, typing→frame p95 < 34 ms. Comment results on #51.

## 2. Rust sender with a hardware encoder (the key measurement)
There's no Windows version of the Swift probe, so port its sender to Rust as `client/crates/snacka-core/examples/send.rs`:
- **Synthetic source:** same as `spikes/stream-probe/Sources/StreamProbe/FrameSource.swift` (scrolling texture with rectangles and a noisy band) plus the barcode writer from `Barcode.swift`. Put the barcode writer in `barcode.rs` next to the reader, with a round-trip unit test.
- **Hardware encoder**, lowest latency. Preferred order:
  1. **NVENC directly** (NVIDIA Video Codec SDK through FFI or a crate): ultra-low-latency tuning (`NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY`), no B-frames, P1–P4 presets, infinite GOP with on-demand IDR, and optionally sliced output.
  2. **Media Foundation** H264/HEVC encoder MFT (vendor-neutral: AMD/Intel/NVIDIA) with `CODECAPI_AVLowLatencyMode`.

  Feed it D3D11 NV12 textures, so there's no CPU copy at 4K.
- **Output:** the existing wire format (`wire.rs`: stream id, keyframe flag, AVCC NAL units with parameter sets on keyframes). Honour keyframe requests (`SNKR`).
- **Report:** encode time per frame (p50/p99) and achieved fps, like the Swift `send` mode.

## 3. Hardware decoder + receiver
- `client/crates/snacka-core/src/stream/d3d11.rs` (or `mediafoundation.rs`): decode H264/HEVC to **NV12 D3D11 textures** using D3D11 video decoding (directly via `ID3D11VideoDecoder`, or MF with `IMFDXGIDeviceManager`). Provide the same shape as `videotoolbox.rs`: `Decoder::decode(keyframe, parameter_sets, avcc) -> Option<DecodedFrame>`, plus `with_luma` (via a staging texture, test mode only) for the barcode.
- Make `VideoReceiver` (in `stream/mod.rs`) work on Windows with this decoder.
- Run `examples/recv` against your sender in loopback. Produce the same matrix table as `spikes/stream-probe/results-m4pro.md`, but for NVENC/MF, and commit it as `client/results/windows-<gpu>.md`.

## 4. Native video surface in the Tauri window
- `client/src-tauri/src/video_surface_windows.rs`, same commands as macOS (`start_test_stream`, `set_video_rect` with `viewport_height`).
- Presentation: a **child HWND above the WebView2 window**, with a DXGI **flip-model** swap chain (`FLIP_DISCARD`, frame-latency waitable object, `SetMaximumFrameLatency(1)`), and `ID3D11VideoProcessor` for NV12 → BGRA scaling with aspect fit. Alternatively use DirectComposition visuals if the child HWND fights WebView2 z-order.
- Handle DPI scaling (CSS px × device scale factor = physical px), resize, move, maximise and fullscreen.
- Verify with screenshots that the video sits exactly in the black box (see the macOS commit `bfe86bd` for the title-bar offset lesson). Report capture→present latency in loopback.

## 5. Cross-machine
- Mac sender (`spikes/stream-probe ... send --host <windows-ip>`) → Windows receiver over the LAN: fps, drops, keyframe recovery (start the receiver mid-stream; restart the sender). Latency isn't comparable across machines without clock sync; note that in the report.

## Done when
- [ ] UI benchmark numbers (WebView2 + Chrome) on #51
- [ ] Encode/decode latency matrix for this GPU committed and summarised on #49, especially **4K120 HEVC/AV1 encode p50/p99**
- [ ] Native surface works in the Tauri window on Windows (screenshot in the PR)
- [ ] Follow-up issues filed for anything not finished
