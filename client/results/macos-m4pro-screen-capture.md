# macOS results: real screen capture, M4 Pro (#49)

**Machine:** MacBook Pro, Apple M4 Pro, built-in Liquid Retina XDR (3024×1964, ProMotion, max 120 Hz), macOS 27.2.
**Caveat for this run:** on battery at 24 % with **Low Power Mode on**, which caps ProMotion at **60 Hz**,
so everything below is at 60 fps and somewhat noisy. Rerun on mains power with Low Power Mode off for 120 Hz.

## What's measured

`spikes/stream-probe` `screen` mode, all on one Mac (loopback UDP):

1. An always-on-top window at the display's top-left draws the barcode (frame number + the time it
   was drawn) on every display refresh (CADisplayLink asking for 120 Hz). With `--motion`, a window
   under it fills the captured region with the synthetic "game" texture scrolling 12 px per frame
   (scrolled by the window server on the GPU, so it costs no CPU).
2. ScreenCaptureKit captures that region 1:1 in NV12 (`420v`, what VideoToolbox takes without conversion).
3. The sender reads the barcode back from the captured pixels and encodes with VideoToolbox (no
   B-frames, low-latency rate control, real-time), sends over UDP, decodes with VideoToolbox, and
   reads the barcode from the decoded picture.

| Column | From → to |
|---|---|
| capture | drawn by the app → captured frame delivered (window server composition + ScreenCaptureKit) |
| encode | captured frame delivered → bitstream out |
| decode | received → decoded picture |
| total | **drawn by the app → decoded picture**: everything except the viewer's own scanout |

Run: `swift build -c release && .build/release/StreamProbe screen --width 1920 --height 1080 --fps 120 --codec hevc --bitrate 30 --motion --seconds 12`
(needs Screen Recording permission for the terminal).

## Results (60 Hz, `--motion`, p50 / p99 ms)

| Codec | Capture size | capture | encode | decode | total | fps | Mbps |
|---|---|---|---|---|---|---|---|
| H.264 | 1920×1080 | 5.1 / 14.7 | 6.6 / 16.7 | 1.1 / 7.4 | **13.9** / 40.1 | 60 | 9.6 |
| HEVC | 1920×1080 | 5.0 / 14.0 | 6.8 / 9.9 | 0.8 / 3.2 | **13.5** / 27.1 | 60 | 8.6 |
| H.264 | 2560×1440 | 13.3 / 36.9 | 8.6 / 12.7 | 1.3 / 2.8 | **24.0** / 60.4 | 60 | 12.8 |
| HEVC | 2560×1440 | 12.7 / 19.9 | 9.2 / 12.4 | 1.2 / 4.1 | **23.8** / 40.0 | 60 | 9.4 |
| H.264 ¹ | 3024×1964 (native) | 4.3 / 13.3 | 14.7 / 61.3 | 1.6 / 4.7 | **21.4** / 150.4 | 60 | 23.9 |
| HEVC | 3024×1964 (native) | 4.1 / 12.9 | 15.5 / 42.2 | 1.5 / 1.7 | **21.5** / 61.2 | 60 | 13.9 |

No dropped frames, decode errors or unreadable barcodes in any run. Static desktop content (no
`--motion`) gives about the same latency at a fraction of the bitrate.

¹ VideoToolbox's H.264 **low-latency rate control produces no frames at all above about 4 MP** (also in
the synthetic `loop` mode: 3 fps). Without it, H.264 works, with worse p99. Use HEVC for large captures on Macs.

## Takeaways

- **Real capture works and is fast on macOS.** Window server composition + ScreenCaptureKit adds about
  4–6 ms (p50) at 60 Hz, roughly a third of a frame, on top of the encoder. The 1440p runs' 13 ms
  capture p50 is the noisy part of this battery run, not a resolution effect (native res is lower).
- **The M4 Pro encoder is the bottleneck for 4K-class streams**, as in the synthetic runs: ~15 ms per
  native-resolution frame, so a Mac can host about 1080p120 / 1440p60–90, not 4K120. Windows (NVENC,
  3–4 ms at 4K) and Linux (VAAPI, ~8 ms at 4K) host the high tiers.
- **Next:** rerun at 120 Hz on mains power, then move capture into the Rust core
  (`snacka-core`, ScreenCaptureKit through objc bindings) with the same barcode check, and a
  cross-machine run (Mac sender → Windows/Linux viewer, #59/#66).
