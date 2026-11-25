# Snacka client (new)

See [ADR 0001](../docs/adr/0001-client-architecture.md) and epic #54. One TypeScript UI for web, desktop and Android (Tauri 2), plus a Rust core.

| Path | What |
|---|---|
| `ui/` | React + TypeScript + Vite. Runs in the browser today. |
| `src-tauri/` | Tauri 2 shell (coming in spike #51) |
| `crates/snacka-core/` | Rust core: LiveKit, game-streaming engine, input (coming) |

## Run the web UI against a local server

```bash
docker compose -f ../docker-compose.dev.yml up -d           # PostgreSQL
JWT_SECRET_KEY=$(openssl rand -base64 48) ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project ../src/Snacka.Server --urls http://localhost:5117
cd ui && pnpm install && pnpm dev                              # http://localhost:5173
```

On a fresh server, the first account becomes admin. It needs the invite code printed in the server console.

Regenerate the typed API client after server API changes: `pnpm gen:api` (needs the server running).

## Linux

Needs the Tauri prerequisites plus VAAPI and FFmpeg development files (the Rust core links the system libavcodec and generates libva bindings with bindgen):

```bash
# Fedora (H.264/HEVC need RPM Fusion's ffmpeg and mesa-va-drivers-freeworld)
sudo dnf install webkit2gtk4.1-devel gtk3-devel libsoup3-devel openssl-devel librsvg2-devel libxdo-devel \
  clang-devel libva-devel ffmpeg-devel
# Debian/Ubuntu
sudo apt install libwebkit2gtk-4.1-dev libgtk-3-dev libssl-dev librsvg2-dev libxdo-dev \
  libclang-dev libva-dev libavcodec-dev libavutil-dev
```

On immutable distros (Bazzite, Silverblue) build inside a distrobox/toolbox with those packages. `cargo test -p snacka-core` includes a VAAPI encode/decode round trip on the GPU (skipped without a VAAPI device). Results and how to run the matrix: [results/linux-radeon-8060s.md](results/linux-radeon-8060s.md).

## Android

Needs a JDK 17+ and the Android SDK + NDK (on this Mac: `brew install openjdk@21 android-commandlinetools`).

```bash
export JAVA_HOME=/opt/homebrew/opt/openjdk@21/libexec/openjdk.jdk/Contents/Home
export ANDROID_HOME=/opt/homebrew/share/android-commandlinetools
export NDK_HOME=$ANDROID_HOME/ndk/27.0.12077973
rustup target add aarch64-linux-android
pnpm tauri android build --debug --target aarch64 --apk   # -> src-tauri/gen/android/app/build/outputs/apk/
```

From the Android emulator, the dev server on the Mac is `http://10.0.2.2:5117`. From a phone on the same Wi-Fi, run the server with `--urls http://0.0.0.0:5117` and use the Mac's LAN IP.
