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
