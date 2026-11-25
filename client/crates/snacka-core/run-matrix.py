#!/usr/bin/env python3
"""Runs the Rust `send` + `recv` examples in loopback over a resolution/fps/codec matrix and
prints a markdown table (the Windows/Linux counterpart of spikes/stream-probe/run-matrix.py).

Build first:  cargo build --release -p snacka-core --examples   (from client/)
Usage:        python run-matrix.py [--seconds 8] [--codecs h264,hevc,av1] [--presets 1] [--port 9123]
"""
import argparse, json, os, subprocess, sys, time

here = os.path.dirname(os.path.abspath(__file__))
exe = ".exe" if os.name == "nt" else ""
bin_dir = os.path.join(here, "..", "..", "target", "release", "examples")
send, recv = (os.path.join(bin_dir, n + exe) for n in ("send", "recv"))

p = argparse.ArgumentParser()
p.add_argument("--seconds", type=float, default=8)
p.add_argument("--warmup", type=float, default=1)
p.add_argument("--codecs", default="h264,hevc,av1")
p.add_argument("--presets", default="1", help="comma-separated NVENC presets, e.g. 1,4")
p.add_argument("--port", type=int, default=9123)
a = p.parse_args()

# (width, height, fps, bitrate Mbps): the same tiers as the macOS matrix.
tiers = [(1920, 1080, 60, 20), (1920, 1080, 120, 30), (2560, 1440, 120, 50), (3840, 2160, 60, 60), (3840, 2160, 120, 100)]


def ms(stage, q):
    v = stage.get(q) if stage else None
    return "–" if v is None else f"{v:.1f}"


rows = []
for (w, h, fps, br) in tiers:
    for codec in a.codecs.split(","):
        for preset in a.presets.split(","):
            r = subprocess.Popen([recv, str(a.port), "--seconds", str(a.seconds), "--warmup", str(a.warmup)],
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            time.sleep(0.8)  # let the receiver bind and create its device
            s = subprocess.run([send, "--port", str(a.port), "--width", str(w), "--height", str(h), "--fps", str(fps),
                                "--codec", codec, "--bitrate", str(br), "--preset", preset,
                                "--seconds", str(a.seconds + a.warmup + 0.5), "--warmup", str(a.warmup)],
                               capture_output=True, text=True, timeout=a.seconds + 60)
            try:
                rout, rerr = r.communicate(timeout=30)
            except subprocess.TimeoutExpired:
                r.kill()
                rout, rerr = r.communicate()
            sline = next((l for l in s.stdout.splitlines() if l.startswith("SUMMARY ")), None)
            rline = next((l for l in rout.splitlines() if l.startswith("SUMMARY ")), None)
            mode = f"{w}x{h}@{fps}"
            if not sline or not rline:
                err = (s.stderr + rerr).strip().replace("\n", " ")[-150:]
                rows.append(f"| {mode} | {codec} | P{preset} | FAILED: {err} ||||||||")
            else:
                sx, rx = json.loads(sline[8:]), json.loads(rline[8:])
                rows.append(
                    f"| {mode} | {codec} | P{preset} | {sx['fps']:.0f} / {rx['fps']:.0f} "
                    f"| {sx['encode_ms']['p50']:.1f} / {sx['encode_ms']['p99']:.1f} "
                    f"| {ms(rx['decode_ms'], 'p50')} / {ms(rx['decode_ms'], 'p99')} "
                    f"| **{ms(rx['total_ms'], 'p50')}** / {ms(rx['total_ms'], 'p95')} / {ms(rx['total_ms'], 'p99')} "
                    f"| {sx['mbps']:.0f} | {sx['keyframes']} / {rx['keyframe_requests']} | {rx['dropped']} | {rx['bad']} |")
            print(rows[-1], file=sys.stderr, flush=True)

print("| Mode | Codec | Preset | fps sent / decoded | Encode p50/p99 ms | Decode p50/p99 ms | **Total p50**/p95/p99 ms "
      "| Mbps | Keyframes / requests | Dropped | Bad barcode |")
print("|---|---|---|---|---|---|---|---|---|---|---|")
print("\n".join(rows))
