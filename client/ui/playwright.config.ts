import { defineConfig } from "@playwright/test";
import { mkdirSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

/**
 * End-to-end tests: real UI in real Chrome against a running Snacka server (+ LiveKit for voice).
 *   SNACKA_SERVER=http://localhost:5117 pnpm e2e
 * Uses the installed Google Chrome (no browser download) with a fake camera and a fake
 * microphone that plays a continuous tone (Chrome's default fake mic only beeps briefly, which
 * makes "is speaking" detection flaky).
 */
function writeToneWav(path: string, seconds = 10, hz = 440, rate = 48_000): string {
  const samples = seconds * rate;
  const buf = Buffer.alloc(44 + samples * 2);
  buf.write("RIFF", 0);
  buf.writeUInt32LE(36 + samples * 2, 4);
  buf.write("WAVEfmt ", 8);
  buf.writeUInt32LE(16, 16);
  buf.writeUInt16LE(1, 20); // PCM
  buf.writeUInt16LE(1, 22); // mono
  buf.writeUInt32LE(rate, 24);
  buf.writeUInt32LE(rate * 2, 28);
  buf.writeUInt16LE(2, 32);
  buf.writeUInt16LE(16, 34);
  buf.write("data", 36);
  buf.writeUInt32LE(samples * 2, 40);
  for (let i = 0; i < samples; i++) buf.writeInt16LE(Math.round(Math.sin((2 * Math.PI * hz * i) / rate) * 12_000), 44 + i * 2);
  mkdirSync(join(path, ".."), { recursive: true });
  writeFileSync(path, buf);
  return path;
}

const tone = writeToneWav(join(tmpdir(), "snacka-e2e", "tone-440hz.wav"));

export default defineConfig({
  testDir: "./e2e",
  timeout: 60_000,
  workers: 1,
  reporter: [["list"]],
  use: {
    baseURL: "http://localhost:5174",
    channel: "chrome",
    permissions: ["microphone", "camera"],
    launchOptions: {
      args: [
        "--use-fake-ui-for-media-stream",
        "--use-fake-device-for-media-stream",
        `--use-file-for-fake-audio-capture=${tone}`,
        "--autoplay-policy=no-user-gesture-required",
      ],
    },
  },
  webServer: { command: "pnpm dev --port 5174 --strictPort", url: "http://localhost:5174", reuseExistingServer: true },
});
