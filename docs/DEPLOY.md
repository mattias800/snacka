# Deployment Guide

This guide covers how to deploy Snacka server and client applications.

## Prerequisites

### All Platforms
- .NET 9 SDK or later
- Git

### Client Dependencies

The client requires the following native libraries for media playback and capture:

#### macOS
```bash
# Install FFmpeg, SDL2, and VLC via Homebrew
brew install ffmpeg sdl2
brew install --cask vlc
```

#### Windows
- FFmpeg: Download from https://ffmpeg.org/download.html and add to PATH
- SDL2: Usually bundled or download from https://www.libsdl.org/
- VLC: Download and install from https://www.videolan.org/vlc/

#### Linux (Ubuntu/Debian)
```bash
sudo apt update
sudo apt install ffmpeg libsdl2-dev vlc libvlc-dev
```

**Note:** VLC is required for inline audio playback of audio file attachments. The client uses LibVLCSharp which leverages the system-installed VLC for codec support.

### LibVLC Audio Playback (macOS arm64)

The `VideoLAN.LibVLC.Mac` NuGet package only provides x86_64 libraries. On Apple Silicon Macs (M1/M2/M3), you must use the system-installed VLC.app which is arm64 native.

**Important:** The `VLC_PLUGIN_PATH` environment variable must be set **before** the .NET process starts. Setting it via `Environment.SetEnvironmentVariable()` after startup doesn't work because the dynamic linker has already processed the libraries.

#### Running the Client with Audio Support

```bash
# Option 1: Use the wrapper script (recommended)
./run-client.sh

# Option 2: Use dev-start.sh (sets VLC env vars automatically)
./dev-start.sh

# Option 3: Set environment variables manually
VLC_PLUGIN_PATH=/Applications/VLC.app/Contents/MacOS/plugins \
DYLD_LIBRARY_PATH=/Applications/VLC.app/Contents/MacOS/lib \
dotnet run --project src/Snacka.Client/Snacka.Client.csproj
```

#### Testing Audio Playback

Place an MP3 file in `~/Downloads` and run:
```bash
./run-client.sh --audio-test
```

#### Production Distribution

For distributed applications, the launcher (shell script, .app bundle, or installer) must set `VLC_PLUGIN_PATH` before starting the .NET runtime:

**macOS (.app bundle Info.plist or launcher script):**
```bash
export VLC_PLUGIN_PATH="/Applications/VLC.app/Contents/MacOS/plugins"
export DYLD_LIBRARY_PATH="/Applications/VLC.app/Contents/MacOS/lib"
```

**Windows (batch file or installer):**
```batch
set VLC_PLUGIN_PATH=%ProgramFiles%\VideoLAN\VLC\plugins
```

**Linux:**
VLC plugins are typically in standard system locations (`/usr/lib/vlc/plugins`) and are found automatically.

## Development Setup

### Quick Start

The easiest way to run Snacka for development is using the provided script:

```bash
./dev-start.sh
```

This will:
1. Build both server and client
2. Start the server on `http://localhost:5117`
3. Start two client instances with test accounts (Alice and Bob)

### Manual Development Setup

#### 1. Start the Server

```bash
cd src/Snacka.Server
dotnet run
```

The server starts on `http://localhost:5117` by default.

#### 2. Start a Client

```bash
cd src/Snacka.Client
dotnet run -- --server http://localhost:5117
```

Optional client arguments:
- `--server <url>` - Server URL (default: http://localhost:5117)
- `--email <email>` - Auto-login with this email
- `--password <password>` - Auto-login password
- `--title <title>` - Window title
- `--profile <name>` - Profile name for settings storage

## Production Deployment

### Server Deployment

#### 1. Configure Production Settings

Create `appsettings.Production.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "UseSqlite": false,
  "ConnectionStrings": {
    "DefaultConnection": "Server=your-db-server;Database=Snacka;User Id=snacka;Password=your-password;"
  },
  "Jwt": {
    "SecretKey": "YOUR-PRODUCTION-SECRET-KEY-AT-LEAST-32-CHARACTERS-LONG!",
    "Issuer": "Snacka",
    "Audience": "Snacka",
    "AccessTokenExpirationMinutes": 60,
    "RefreshTokenExpirationDays": 7
  },
  "ServerInfo": {
    "Name": "Your Server Name",
    "Description": "Your server description",
    "AllowRegistration": true
  },
  "Tenor": {
    "ApiKey": "YOUR_TENOR_API_KEY",
    "ClientKey": "snacka"
  }
}
```

**Important Security Notes:**
- Change the `Jwt:SecretKey` to a strong, unique secret (minimum 32 characters)
- Use environment variables for sensitive values in production
- Set `AllowRegistration` to `false` if you don't want open registration

**Optional Features:**
- **GIF Picker:** To enable the GIF search/picker feature, add a Tenor API key. Get a free key at https://developers.google.com/tenor/guides/quickstart

#### 2. Build for Production

```bash
dotnet publish src/Snacka.Server/Snacka.Server.csproj \
  -c Release \
  -o ./publish/server
```

#### 3. Database Options

**SQLite (Simple, Single Server)**
```json
{
  "UseSqlite": true
}
```
The database file `snacka.db` will be created in the working directory.

**PostgreSQL (Recommended for Production)**
```json
{
  "UseSqlite": false,
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=snacka;Username=snacka;Password=your-password"
  }
}
```

#### 4. Run the Server

```bash
cd ./publish/server
dotnet Snacka.Server.dll
```

Or with environment-specific configuration:
```bash
ASPNETCORE_ENVIRONMENT=Production dotnet Snacka.Server.dll
```

#### 5. Reverse Proxy Setup (Recommended)

For production, run behind a reverse proxy for SSL termination. The server is designed to work behind proxies and correctly handles:
- `X-Forwarded-For` - Real client IP (for rate limiting and logging)
- `X-Forwarded-Proto` - Original protocol (https)
- WebSocket connections (for real-time messaging via SignalR)

**NGINX Proxy Manager**

NGINX Proxy Manager works out of the box - just create a proxy host pointing to `snacka-server:8080` (or `localhost:5117` if running outside Docker) and enable SSL. WebSocket support is enabled by default.

**Manual NGINX Configuration**

```nginx
server {
    listen 443 ssl http2;
    server_name snacka.example.com;

    ssl_certificate /path/to/cert.pem;
    ssl_certificate_key /path/to/key.pem;

    location / {
        proxy_pass http://localhost:5117;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # WebSocket support for SignalR
        proxy_read_timeout 86400;
    }
}
```

### Client Distribution

#### Build for All Platforms

**macOS:**
```bash
dotnet publish src/Snacka.Client/Snacka.Client.csproj \
  -c Release \
  -r osx-x64 \
  --self-contained true \
  -o ./publish/client-macos-x64

# For Apple Silicon
dotnet publish src/Snacka.Client/Snacka.Client.csproj \
  -c Release \
  -r osx-arm64 \
  --self-contained true \
  -o ./publish/client-macos-arm64
```

**Windows:**
```bash
dotnet publish src/Snacka.Client/Snacka.Client.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -o ./publish/client-windows-x64
```

**Linux:**
```bash
dotnet publish src/Snacka.Client/Snacka.Client.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -o ./publish/client-linux-x64
```

### Docker Deployment (Server)

The easiest way to deploy Snacka is using Docker Compose with pre-built images.

#### Quick Start with Docker Compose

1. **Configure environment variables:**
   ```bash
   cp .env.example .env
   ```

2. **Edit `.env` and set the required values:**
   - `JWT_SECRET_KEY` - A secure random string (min 32 characters)
   - `POSTGRES_PASSWORD` - A strong database password
   - `ALLOWED_ORIGIN` - Your domain (e.g., `https://snacka.example.com`)

3. **Start the server:**
   ```bash
   docker compose up -d
   ```

4. **Check status:**
   ```bash
   docker compose ps
   docker compose logs -f
   ```

5. **Stop the server:**
   ```bash
   docker compose down
   ```

Data is persisted in Docker volumes (`postgres-data` for database, `snacka-uploads` for files).

#### Environment Variables (.env file)

| Variable | Description | Required |
|----------|-------------|----------|
| `JWT_SECRET_KEY` | JWT signing key (min 32 chars) | **Yes** |
| `POSTGRES_PASSWORD` | PostgreSQL password | **Yes** |
| `ALLOWED_ORIGIN` | CORS allowed origin (your domain) | **Yes** |
| `SERVER_NAME` | Server display name | No (default: Snacka Server) |
| `SERVER_DESCRIPTION` | Server description | No |
| `ALLOW_REGISTRATION` | Allow new user signups | No (default: true) |
| `TENOR_API_KEY` | Tenor GIF API key (optional) | No |

#### Using Pre-built Images

The docker-compose.yml is configured to use pre-built images from GitHub Container Registry:

```bash
# Pull latest image
docker pull ghcr.io/mattias800/snacka:latest

# Run with docker compose
docker compose up -d
```

#### Building Locally

If you prefer to build locally instead of using pre-built images, edit `docker-compose.yml`:

```yaml
services:
  snacka-server:
    # Comment out the image line
    # image: ghcr.io/mattias800/snacka:latest
    # Uncomment the build section
    build:
      context: .
      dockerfile: Dockerfile
```

Then build and run:

```bash
docker compose build
docker compose up -d
```

#### Manual Docker Run

For production deployments, use docker compose (shown above) which includes PostgreSQL.

For quick testing with SQLite (not recommended for production):

```bash
docker run -d -p 5117:8080 \
  --name snacka-server \
  -e UseSqlite=true \
  -e Jwt__SecretKey=your-secret-key-at-least-32-characters \
  -v snacka-data:/app/data \
  -v snacka-uploads:/app/uploads \
  ghcr.io/mattias800/snacka:latest
```

## Environment Variables

The server supports configuration via environment variables:

| Variable | Description |
|----------|-------------|
| `ASPNETCORE_ENVIRONMENT` | Environment name (Development/Production) |
| `ASPNETCORE_URLS` | Server URLs (e.g., `http://0.0.0.0:5117`) |
| `Jwt__SecretKey` | JWT signing key (min 32 characters) |
| `ConnectionStrings__DefaultConnection` | PostgreSQL connection string |
| `UseSqlite` | Use SQLite instead of PostgreSQL (dev only) |
| `AllowedOrigins__0` | CORS allowed origin |

## Health Check

The server exposes a health check endpoint:

```bash
curl http://localhost:5117/api/health
```

## Troubleshooting

### Server won't start
- Check that port 5117 is not in use
- Verify database connection string
- Check logs for specific error messages

### Client can't connect
- Verify server URL is correct
- Check firewall settings
- Ensure WebSocket connections are allowed through any proxy

### Audio/Video issues
- Verify FFmpeg is installed and in PATH
- Check SDL2 installation
- On macOS, ensure microphone/camera permissions are granted

### Database errors
- For SQLite: ensure write permissions in the directory
- For SQL Server: verify connection string and credentials
- Run migrations if using a fresh database

## Ports and Firewall

| Port | Protocol | Purpose |
|------|----------|---------|
| 5117 | TCP | HTTP API and SignalR |

WebRTC media uses dynamic UDP ports. For production behind NAT, consider setting up a TURN server.
