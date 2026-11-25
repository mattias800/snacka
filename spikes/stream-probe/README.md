# stream-probe (spike for #49)

Measures the **latency floor** of a game-streaming pipeline:
synthetic frame → hardware encode (VideoToolbox) → custom UDP transport (no jitter buffer) → hardware decode → read barcode from the decoded pixels.

Each frame carries its frame number and capture timestamp **inside the image** (a 64-bit barcode in the luma plane). Any receiver that can decode frames can therefore measure end-to-end latency of a black-box stack, such as LiveKit or the current SIPSorcery path.

```bash
swift build -c release
.build/release/StreamProbe loop --width 2560 --height 1440 --fps 120 --codec hevc --bitrate 50 --seconds 10
python3 run-matrix.py        # full resolution/codec matrix as a markdown table
```

"total" = capture timestamp → frame decoded on the receiver. It does **not** include real screen capture (about 1 frame with ScreenCaptureKit/DXGI) or display presentation (up to 1 refresh). Loopback only, so network time is near zero; on a LAN add 0.5–2 ms.

## Results: Apple M4 Pro, macOS 27.2, loopback (2026-11-25)

| Mode | Codec | Low-latency RC | fps | Encode p50/p99 ms | Decode p50/p99 ms | **Total p50**/p95/p99 ms |
|---|---|---|---|---|---|---|
| 1080p60 | hevc | off | 60 | 4.5 / 5.8 | 0.7 / 1.8 | **5.6** / 7.4 / 8.6 |
| 1080p120 | hevc | on | 121 | 5.7 / 6.6 | 0.6 / 1.6 | **6.5** / 7.1 / 10.5 |
| 1080p120 | h264 | on | 121 | 5.4 / 15.3 | 0.7 / 3.7 | **6.4** / 9.8 / 25.7 |
| 1440p120 | h264 | on | 122 | 7.6 / 8.4 | 0.9 / 1.9 | **8.8** / 9.4 / 21.0 |
| 1440p120 | hevc | off | 121 | 7.1 / 10.5 | 0.9 / 1.9 | **8.3** / 10.7 / 40.5 |
| 4K60 | hevc | off | 61 | 14.8 / 18.2 | 1.7 / 5.1 | **16.9** / 22.4 / 51.0 |
| 4K60 | hevc | on | 61 | 17.5 / 18.0 | 1.7 / 2.1 | **19.5** / 19.8 / 30.7 |
| 4K120 | hevc | on | **60** (drops half) | 17.5 / 23.7 | 1.8 / 8.4 | 19.5 / 22.6 / 39.5 |
| 4K120 | hevc | off | 122 | 82.7 / 94.1 | 1.9 / 13.4 | 86.1 (queueing) |
| 4K120 | h264 | on | – | encoder refuses 4K120 in low-latency mode | | |

The full 20-run matrix is in `results-m4pro.md`.

### Findings
1. **Up to 1440p120, a Mac host is excellent:** 6–9 ms median from capture to decoded frame, before display.
2. **At 4K, the M4 Pro's single media engine takes about 15–18 ms per frame.** 4K60 works (~17–20 ms). **4K120 isn't achievable on this chip:** it either halves the frame rate or queues frames (85–145 ms). Disabling `RealTime` and lowering `Quality` made no difference. This is a hardware limit, not a software one; Max/Ultra chips have two engines and may differ.
3. **Decode is cheap everywhere:** 1–2 ms even at 4K.
4. **Low-latency rate control** mostly tightens p99 (fewer spikes), not p50. It disables `MaxFrameDelayCount` and `PrioritizeEncodingSpeedOverQuality` (`-12900`).
5. **Implication:** 4K120 hosting is realistically a Windows/Linux + NVIDIA/AMD feature (NVENC encodes 4K in a few ms). Measure that next on real hardware (#48).
