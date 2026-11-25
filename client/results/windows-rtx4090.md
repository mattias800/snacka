# Windows results: i7-13700K + RTX 4090

**Machine:** Intel Core i7-13700K, 64 GB RAM, NVIDIA GeForce RTX 4090 (driver 610.88), Windows 11 Pro 26300.
Display 3840×2160 @ 240 Hz, 150 % scaling (devicePixelRatio 1.5). Desktop on mains power, "Balanced" power plan.

## UI benchmark (#51)

10k messages, `client/ui/src/Bench.tsx`. Tauri debug build (`pnpm tauri build --debug --no-bundle`) with WebView2
(Edge 158), and Chrome 156 on the production build (`pnpm --dir ui preview`, `#bench`). Three runs each; the first
run of each is a cold start.

| Runtime | Viewport | Mount (cold / warm) | Scroll fps (p99) | Append fps | Typing→frame p50 / p95 | Hitches >25 ms (cold / warm) |
|---|---|---|---|---|---|---|
| WebView2 (Tauri) | 1280×800 | 118 ms / 43–48 ms | 240 (4.3 ms) | 240 | 4.2 / 4.3 ms | 1 / 0 |
| Chrome 156 | 1266×763 | 281 ms / 48 ms | 239 (4.3 ms) | 240 | 4.2 / 4.3 ms | 1 / 0 |

**Pass** on every criterion (scroll ≥ 55 fps, ≤ 5 hitches, mount < 100 ms warm, typing p95 < 34 ms). Unlike
WKWebView on macOS (capped near 60 Hz), WebView2 renders at the display's full 240 Hz, the same as Chrome. Only the
cold first mount (118 ms, JIT and font warm-up) is above 100 ms; Chrome's cold mount is slower still.
