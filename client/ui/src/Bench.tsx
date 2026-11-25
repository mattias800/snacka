import { useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { MessageList, type Message } from "./MessageList";
import { inNativeApp } from "./StreamTest";

/**
 * UI performance benchmark (#51): measures the real message list in whatever webview runs it
 * (Chrome, WKWebView, WebView2, WebKitGTK), so Linux/WebKitGTK can be compared with the rest.
 *
 * Open with `#bench` in a browser, or start the desktop app with SNACKA_BENCH=1
 * (prints `BENCH {json}` to stdout; SNACKA_BENCH_EXIT=1 quits afterwards).
 * The window must be visible: hidden windows throttle requestAnimationFrame.
 */

const MESSAGE_COUNT = 10_000;
const WORDS = "lorem ipsum dolor sit amet snacka voice stream game ping lag frame codec rust tauri hello gg wp".split(" ");

function makeMessages(n: number, offset = 0): Message[] {
  let seed = 42 + offset;
  const rand = () => ((seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648);
  const names = ["alice", "bob", "carol", "dave", "eve"];
  return Array.from({ length: n }, (_, i) => {
    const words = 3 + Math.floor(rand() * (rand() < 0.1 ? 120 : 25));
    const content = Array.from({ length: words }, () => WORDS[Math.floor(rand() * WORDS.length)]).join(" ");
    const author = names[(i + offset) % names.length];
    return {
      id: `bench-${offset + i}`,
      content: rand() < 0.05 ? `${content}\n${content}` : content,
      authorId: author,
      authorName: author,
      createdAt: new Date(Date.UTC(2026, 10, 25, 12, 0, 0) + (offset + i) * 7000).toISOString(),
      edited: false,
      reactions: null,
      replyTo: null,
    };
  });
}

const nextFrame = () => new Promise<number>((r) => requestAnimationFrame(r));

function frameStats(intervals: number[]) {
  const s = [...intervals].sort((a, b) => a - b);
  const p = (q: number) => +(s[Math.min(s.length - 1, Math.floor(s.length * q))] ?? NaN).toFixed(2);
  const total = intervals.reduce((a, b) => a + b, 0);
  return {
    frames: intervals.length,
    fps: +((intervals.length / total) * 1000).toFixed(1),
    p50_ms: p(0.5),
    p95_ms: p(0.95),
    p99_ms: p(0.99),
    // Hitches: frames clearly longer than one 60 Hz refresh (17 ms intervals are just vsync rounding).
    over_25ms: intervals.filter((d) => d > 25).length,
    over_50ms: intervals.filter((d) => d > 50).length,
  };
}

export function Bench() {
  const [messages, setMessages] = useState<Message[]>([]);
  const [draft, setDraft] = useState("");
  const [status, setStatus] = useState("starting…");
  const [result, setResult] = useState<string | null>(null);
  const listRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    let cancelled = false;
    const run = async () => {
      await nextFrame();
      // 1. Mount 10k messages.
      setStatus(`rendering ${MESSAGE_COUNT} messages…`);
      const t0 = performance.now();
      setMessages(makeMessages(MESSAGE_COUNT));
      await nextFrame();
      await nextFrame();
      const mountMs = performance.now() - t0;

      // 2. Scroll top to bottom over 5 s at constant speed.
      setStatus("scrolling…");
      // Scroll the same element the chat view scrolls.
      const el = listRef.current!.querySelector<HTMLElement>(".message-scroller")!;
      el.scrollTop = 0;
      await nextFrame();
      const scrollIntervals: number[] = [];
      const max = el.scrollHeight - el.clientHeight;
      const start = performance.now();
      let last = start;
      while (!cancelled) {
        const now = await nextFrame();
        scrollIntervals.push(now - last);
        last = now;
        const t = (now - start) / 5000;
        el.scrollTop = Math.min(1, t) * max;
        if (t >= 1) break;
      }

      // 3. Incoming messages: append one per frame, keep scrolled to bottom.
      setStatus("appending messages…");
      const appendIntervals: number[] = [];
      last = await nextFrame();
      for (let i = 0; i < 200 && !cancelled; i++) {
        setMessages((prev) => [...prev, ...makeMessages(1, MESSAGE_COUNT + i)]);
        const now = await nextFrame();
        el.scrollTop = el.scrollHeight;
        appendIntervals.push(now - last);
        last = now;
      }

      // 4. Typing: state update per keystroke, measured until the next frame.
      setStatus("typing…");
      const typing: number[] = [];
      for (let i = 0; i < 100 && !cancelled; i++) {
        const t = performance.now();
        setDraft((d) => d + WORDS[i % WORDS.length][0]);
        await nextFrame();
        typing.push(performance.now() - t);
      }

      const report = {
        userAgent: navigator.userAgent,
        platform: navigator.platform,
        nativeApp: inNativeApp,
        devicePixelRatio: window.devicePixelRatio,
        viewport: `${window.innerWidth}x${window.innerHeight}`,
        messages: MESSAGE_COUNT,
        mount_ms: +mountMs.toFixed(1),
        scroll: frameStats(scrollIntervals),
        append: frameStats(appendIntervals),
        typing_to_frame: frameStats(typing),
      };
      const json = JSON.stringify(report);
      console.log("BENCH " + json);
      setResult(JSON.stringify(report, null, 2));
      setStatus("done");
      if (inNativeApp) await invoke("report_bench", { json });
    };
    void run();
    return () => {
      cancelled = true;
    };
  }, []);

  return (
    <div className="bench">
      <header>
        UI benchmark · {status}
        {result && <button onClick={() => void navigator.clipboard.writeText(result)}>Copy results</button>}
      </header>
      {result && <pre>{result}</pre>}
      <div ref={listRef} className="messages bench-list">
        <MessageList messages={messages} />
      </div>
      <input value={draft} onChange={(e) => setDraft(e.target.value)} placeholder="typing test" />
    </div>
  );
}
