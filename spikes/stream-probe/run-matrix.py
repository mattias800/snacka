#!/usr/bin/env python3
"""Runs stream-probe over a resolution/fps/codec matrix and prints a markdown table.

Usage: python3 run-matrix.py [--seconds 8] [--quick]
"""
import json, subprocess, sys, os, argparse

here = os.path.dirname(os.path.abspath(__file__))
probe = os.path.join(here, ".build", "release", "StreamProbe")

parser = argparse.ArgumentParser()
parser.add_argument("--seconds", type=float, default=8)
parser.add_argument("--quick", action="store_true", help="only low-latency RC on")
a = parser.parse_args()

# (width, height, fps, bitrate Mbps) - bitrates roughly what game streaming services use per tier
tiers = [
    (1920, 1080, 60, 20), (1920, 1080, 120, 30),
    (2560, 1440, 120, 50),
    (3840, 2160, 60, 60), (3840, 2160, 120, 100),
]
rows = []
for (w, h, fps, br) in tiers:
    for codec in ("h264", "hevc"):
        for ll in ((True,) if a.quick else (True, False)):
            cmd = [probe, "loop", "--width", str(w), "--height", str(h), "--fps", str(fps), "--codec", codec,
                   "--bitrate", str(br), "--seconds", str(a.seconds), "--port", "9123"]
            if not ll:
                cmd.append("--no-lowlatency")
            out = subprocess.run(cmd, capture_output=True, text=True, timeout=a.seconds + 30)
            line = next((l for l in out.stdout.splitlines() if l.startswith("SUMMARY ")), None)
            if not line:
                rows.append(f"| {w}x{h}@{fps} | {codec} | {'on' if ll else 'off'} | FAILED: {out.stderr.strip()[-120:]} |||||||")
                continue
            s = json.loads(line[len("SUMMARY "):])
            st = s["stages_ms"]
            def g(stage, p): return st.get(stage, {}).get(p, float("nan"))
            dropped = next((l for l in out.stdout.splitlines() if l.startswith("dropped")), "dropped frames: ?").split(":")[1].strip()
            bad = s["counters"].get("badBarcode", 0)
            ll_used = s["config"]["lowLatencyRC"]
            rows.append(f"| {w}x{h}@{fps} | {codec} | {'on' if ll_used else 'off' + (' (unsupported)' if ll else '')} "
                        f"| {s['fps']:.0f} | {g('encode','p50'):.1f} / {g('encode','p99'):.1f} "
                        f"| {g('decode','p50'):.1f} / {g('decode','p99'):.1f} "
                        f"| **{g('total','p50'):.1f}** / {g('total','p95'):.1f} / {g('total','p99'):.1f} "
                        f"| {s['mbps']:.0f} | {dropped} | {bad} |")
            print(rows[-1], file=sys.stderr, flush=True)

print("| Mode | Codec | Low-latency RC | fps | Encode p50/p99 ms | Decode p50/p99 ms | **Total p50**/p95/p99 ms | Mbps | Dropped | Bad barcode |")
print("|---|---|---|---|---|---|---|---|---|---|")
print("\n".join(rows))
