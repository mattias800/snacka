# Agent Guidelines for Snacka

This document provides guidelines for AI agents working on the Snacka project.

## Project Overview

Snacka is a self-hosted Discord alternative for **friends talking and playing games together**, where one friend hosts the server. It runs at friend-group scale, so **quality of service and stability matter more than scale**.

Product requirements:
- **Windows, Linux and macOS:** chat, voice, webcam, screen share, and game streaming / remote play as both **host and viewer**, at the highest achievable quality (up to 4K120) with input lag as close to local play as possible.
- **Android, iOS and iPadOS:** client only (chat, voice, viewing streams). iOS/iPadOS comes later.
- **Web:** browser client.
- Hosting must be easy for a non-expert friend.

## Architecture (in transition, see `docs/adr/0001-client-architecture.md`)

**Target architecture**, tracked in epic #54 with roadmap #45:

| Part | Technology | Location |
|---|---|---|
| UI (web, desktop, Android and iOS from one codebase) | TypeScript, React, Vite | `client/ui` (new) |
| Desktop and Android shell | Tauri 2 | `client/src-tauri` (new) |
| Client core: voice/video via LiveKit, game-streaming engine, input/virtual gamepads | Rust | `client/crates/snacka-core` (new) |
| Server: accounts, communities, chat, DMs, files, presence, LiveKit tokens | ASP.NET Core + SignalR + EF Core | `src/Snacka.Server` (kept) |
| SFU and TURN | LiveKit server | replaces the custom SFU and coturn |

**Legacy, being replaced (don't extend):**
- `src/Snacka.Client`: the Avalonia client
- `src/Snacka.Server/Services/Sfu`: the custom SIPSorcery SFU
- The P2P hub methods
- The `SnackaCapture*` and `Snacka*Renderer` native helpers
- `turnserver.conf`

Fix them only when needed to keep the old client working. Notes for them are in `docs/legacy/AVALONIA_CLIENT_NOTES.md`.

**Key principles:**
- Game-stream frames never pass through the webview. They are decoded and presented on a native surface owned by the Rust core.
- Anything latency-sensitive or OS-specific lives in the Rust core; the UI stays platform-neutral.
- The browser client uses the LiveKit JS SDK, behind the same interface the Rust core exposes to the UI.

## Current Codebase (Nov 2026)

- **Server** (`src/Snacka.Server`, .NET 9): REST controllers, one large SignalR hub (`Hubs/SnackaHub.cs`), EF Core with SQLite (default, zero-config) or PostgreSQL (Docker), setup wizard, tunnels (port forwarding), gaming stations.
- **Shared models:** `src/Snacka.Shared/Models`.
- **Tests:**
  - `tests/Snacka.Server.Tests` (MSTest; `IntegrationTestBase` uses `WebApplicationFactory`)
  - `tests/Snacka.Client.Tests` (xUnit + Avalonia headless)
  - `tests/Snacka.Client.UnitTests` (xUnit, pure unit tests; kept separate because Avalonia headless initialisation applies to the whole assembly)
- **Spikes:** `spikes/stream-probe`, a Swift latency probe for the game-streaming pipeline (results in its README).
- **CI:** `.github/workflows/test.yml` runs all .NET tests on Linux, Windows and macOS, and the new client's Playwright e2e tests (`client/ui/e2e`) against a real server and LiveKit. Builds and packaging are in `build-client.yml` and `build-server.yml`.

## Code Conventions

### C# (server)
- Use expression-bodied members for one-liners
- Never use `dynamic`; always use proper type annotations and explicit return types
- Prefer interfaces over classes for abstractions
- Every channel- or community-scoped endpoint and hub method must check that the caller is allowed to access it (see #14, #24)

### TypeScript (UI)
- `strict` mode; no `any` (the equivalent of the "no `dynamic`" rule)
- Generate the server API client from OpenAPI rather than hand-writing DTOs
- Talk to the Rust core only through a typed interface that has a browser implementation as well

### Rust (core)
- No `unwrap()`/`expect()` in long-running media or network paths; propagate errors and recover
- No allocation per frame in hot media paths; reuse buffers
- Platform-specific code goes behind traits, with one implementation per OS

## Testing

- **Prefer tests that replace manual testing:**
  - multi-client scenarios against a real server
  - synthetic audio/video sources with measurable output (see the stream-probe barcode approach)
- **When a bug is found manually,** write a test that reproduces it when practical.
- **Current priority is speed and progress.** CI isn't a required gate yet, so don't block work on it, but don't leave the build broken.
- **Run before committing:** `dotnet build` and `dotnet test` for .NET changes; the matching `pnpm`/`cargo` checks for `client/`.

## Workflow

- Commit directly to `master` (no PRs) unless asked otherwise.
- Fix small findings along the way. Larger findings become GitHub issues on `mattias800/snacka`, linked from the roadmap (#45) or the client epic (#54).
- Big decisions go into an ADR in `docs/adr/`.

## Database Conventions

- Use Entity Framework Core migrations
- SQLite (default, self-hosting) and PostgreSQL (Docker) are both supported: avoid provider-specific features, and add every migration for both contexts (`SnackaDbContext` → `Migrations/`, `SqliteSnackaDbContext` → `Migrations/Sqlite/`; see DEVELOPMENT.md). Server tests run on SQLite; the CI e2e job runs on PostgreSQL
- Use proper foreign key relationships
- Index frequently queried columns
- Use soft deletes where appropriate

## Security

- Store passwords using bcrypt (already done)
- JWT access tokens; refresh tokens must be validated and rotated (see #15)
- Validate all user input
- Use HTTPS for all communications
- Sanitize output to prevent XSS
- Known findings are tracked as GitHub issues labelled `security`

## Project Commands

```bash
# Build and test the .NET solution
dotnet build
dotnet test

# Run the server (development)
dotnet run --project src/Snacka.Server

# Create a migration
dotnet ef migrations add MigrationName --project src/Snacka.Server

# Game-streaming latency probe (macOS)
cd spikes/stream-probe && swift build -c release && .build/release/StreamProbe loop --width 2560 --height 1440 --fps 120 --codec hevc
```

## Development Startup (legacy client)

`./dev-start.sh` builds and starts the server on `http://localhost:5117` and two Avalonia clients that log in automatically as **Alice** (`alice@test.com`, usually the community owner) and **Bob** (`bob@test.com`). Client CLI arguments: `--server`, `--email`, `--password`, `--title`, `--profile`.

## When You Make Mistakes

If a user corrects you on something, update this file with the correction so future agents don't make the same mistake.
