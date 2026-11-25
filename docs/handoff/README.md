# Handoff to agents on other machines

Some work needs real hardware: Windows with an NVIDIA GPU, Linux, and Android devices. The owner runs a Claude Code agent on that machine and points it at a **handoff PR**. This file is the shared context; each PR adds a platform file (`WINDOWS.md`, `LINUX.md`, …) with the concrete task list.

## Read first
1. `AGENTS.md`: conventions, workflow, product requirements
2. `docs/adr/0001-client-architecture.md`: TypeScript UI in Tauri 2 + Rust core; video never goes through the webview
3. `spikes/stream-probe/README.md`: the latency method and the macOS results
4. Issues: #51 (client spike, with the benchmark baselines in its comments), #49 (game-streaming engine requirements), #48 (GPU test matrix)

## Where things are
| Path | What |
|---|---|
| `client/ui` | React + TypeScript UI (web, desktop, mobile). `#bench` = UI benchmark, `#stream-test-<port>` = native video test |
| `client/src-tauri` | Tauri 2 app. `video_surface.rs` is the macOS native video overlay; add `video_surface_windows.rs` / `video_surface_linux.rs` next to it |
| `client/crates/snacka-core/src/stream/wire.rs` | **Cross-platform** UDP frame transport (stream ids, keyframe requests). Reuse as is. |
| `client/crates/snacka-core/src/stream/barcode.rs` | **Cross-platform** barcode reader (frame id + capture timestamp in the luma plane) |
| `client/crates/snacka-core/src/stream/videotoolbox.rs` | macOS decoder; the reference for what a platform decoder must provide (`DecodedFrame`, `Decoder::decode`) |
| `client/crates/snacka-core/src/stream/mod.rs` | `VideoReceiver` = receive + decode + recovery; currently `cfg(target_os = "macos")`. Make it generic over a per-OS decoder. |
| `spikes/stream-probe` | Swift sender/receiver (macOS only). The wire format is documented in `Transport.swift` / `wire.rs`. |

## Measurement rules
- **Latency** = capture timestamp (stamped into the pixels as a barcode) → decoded (or → handed to the compositor). This only works when sender and receiver **share a clock**, so measure latency in **loopback on one machine**. Across machines, measure fps, drops, decode time and stability instead (or add clock-offset estimation first).
- Use the **same tiers** as the macOS matrix: 1080p60, 1080p120, 1440p120, 4K60, 4K120, for H264 and HEVC (and AV1 if the GPU encodes it).
- Run on **AC power**, with no battery saver (macOS Low Power Mode capped WebKit at 30 fps and throttled the encoder).
- UI benchmark: `SNACKA_BENCH=1 SNACKA_BENCH_EXIT=1 ./target/debug/snacka-app | grep ^BENCH` after `pnpm tauri build --debug --no-bundle`, window **visible**, size 1280×800. Also run Chrome on `#bench` (via `pnpm --dir ui build && pnpm --dir ui preview`) as the Chromium baseline.

## Reporting
- Commit results as markdown tables next to the code (e.g. `spikes/stream-probe/results-<machine>.md` or a new `client/results/` file), and comment a summary on the relevant issue (#51, #49).
- Push to the PR's branch. When done, mark the PR ready for review; the owner merges.
- File new GitHub issues for anything larger you find, and link them from #54.
- Commit messages end with the `Co-Authored-By` line your harness provides.
