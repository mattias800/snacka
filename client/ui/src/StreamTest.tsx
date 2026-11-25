import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";

/** True when running inside the Tauri desktop/mobile app (not a plain browser). */
export const inNativeApp = typeof window !== "undefined" && "__TAURI_INTERNALS__" in window;

type StreamStats = {
  fps: number;
  width: number;
  height: number;
  decode_p50_ms: number;
  to_layer_p50_ms: number;
  to_layer_p99_ms: number;
  dropped: number;
  bad: number;
};

/**
 * Spike #51: the Rust core decodes a stream-probe stream and shows it on a native layer that
 * sits exactly over the black box below. The web UI only reports where that box is.
 *
 * Send a stream with:
 *   spikes/stream-probe/.build/release/StreamProbe send --port 9300 --width 2560 --height 1440 --fps 120 --codec hevc --bitrate 50 --seconds 60
 */
export function StreamTest({ autoStartPort }: { autoStartPort?: number }) {
  const boxRef = useRef<HTMLDivElement>(null);
  const [port, setPort] = useState(autoStartPort ?? 9300);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [stats, setStats] = useState<StreamStats | null>(null);

  // Keep the native surface aligned with the box on resize, scroll and layout changes.
  useLayoutEffect(() => {
    const el = boxRef.current;
    if (!el || !running) return;
    const sync = () => {
      const r = el.getBoundingClientRect();
      void invoke("set_video_rect", { x: r.left, y: r.top, width: r.width, height: r.height, visible: true });
    };
    sync();
    const observer = new ResizeObserver(sync);
    observer.observe(el);
    window.addEventListener("resize", sync);
    window.addEventListener("scroll", sync, true);
    return () => {
      observer.disconnect();
      window.removeEventListener("resize", sync);
      window.removeEventListener("scroll", sync, true);
      void invoke("set_video_rect", { x: 0, y: 0, width: 0, height: 0, visible: false });
    };
  }, [running]);

  useEffect(() => {
    const unlisten = listen<StreamStats>("stream-stats", (e) => setStats(e.payload));
    return () => {
      void unlisten.then((f) => f());
    };
  }, []);

  useEffect(() => {
    if (autoStartPort) void start();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [autoStartPort]);

  const start = async () => {
    try {
      setError(null);
      await invoke("start_test_stream", { port });
      setRunning(true);
    } catch (e) {
      setError(String(e));
    }
  };

  if (!inNativeApp) return <p className="muted">The stream test needs the desktop app.</p>;

  return (
    <div className="stream-test">
      <div className="stream-controls">
        <label>
          UDP port <input type="number" value={port} onChange={(e) => setPort(Number(e.target.value))} disabled={running} />
        </label>
        <button onClick={() => void start()} disabled={running}>{running ? "Receiving…" : "Start"}</button>
        {error && <span className="error">{error}</span>}
      </div>
      <div ref={boxRef} className="stream-box" />
      {stats && (
        <p className="stream-stats">
          {stats.width}×{stats.height} · {stats.fps.toFixed(0)} fps · decode {stats.decode_p50_ms.toFixed(1)} ms ·
          capture→layer p50 <strong>{stats.to_layer_p50_ms.toFixed(1)} ms</strong> p99 {stats.to_layer_p99_ms.toFixed(1)} ms ·
          dropped {stats.dropped} · bad {stats.bad}
        </p>
      )}
    </div>
  );
}
