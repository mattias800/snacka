# Snacka Development Guide

This guide covers setting up a development environment for Snacka.

## Technology Stack

- **Language**: C# (.NET 9.0)
- **Server**: ASP.NET Core 9
- **Desktop Client**: Avalonia UI 11.3 (cross-platform XAML framework)
- **Real-time Communication**: WebRTC with SipSorcery
- **Signaling**: SignalR for WebSocket-based signaling
- **Database**: Entity Framework Core with PostgreSQL (SQLite for development)
- **Audio**: SDL2 for audio capture and playback
- **Video**: LibVLC for media playback
- **Updates**: Velopack for automatic updates (Windows)
- **Testing**: MSTest

## Project Structure

```
snacka/
├── src/
│   ├── Snacka.Server/          # ASP.NET Core server application
│   ├── Snacka.Client/          # Avalonia UI desktop client (Windows, macOS, Linux)
│   ├── Snacka.Shared/          # Shared models and interfaces
│   ├── Snacka.WebRTC/          # WebRTC/media handling
│   ├── SnackaCapture/          # Screen/window capture (macOS - Swift)
│   ├── SnackaCaptureWindows/   # Screen/window capture (Windows - C++)
│   └── SnackaMetalRenderer/    # Metal-based rendering (macOS - Swift)
├── tests/
│   ├── Snacka.Server.Tests/
│   └── Snacka.WebRTC.Tests/
├── installers/
│   ├── windows/                # Inno Setup scripts
│   ├── macos/                  # DMG creation scripts
│   └── linux/                  # AppImage creation scripts
├── docs/
│   └── DEPLOY.md               # Deployment guide
├── .github/workflows/          # CI/CD workflows
├── Snacka.sln
├── Dockerfile
├── docker-compose.yml          # Production deployment
└── docker-compose.dev.yml      # Development database
```

## Prerequisites

- **.NET 9 SDK** or later
- **Git**
- **Docker** (for PostgreSQL database)

### Platform-Specific Dependencies

#### macOS
- Xcode Command Line Tools (for Swift components)
- VLC (`brew install --cask vlc`)
- FFmpeg (`brew install ffmpeg@6`)

#### Windows
- Visual Studio 2022 or Build Tools (for C++ components)
- CMake
- VLC (optional, for audio playback)

#### Linux
- VLC and development libraries (`sudo apt install vlc libvlc-dev`)
- FFmpeg (`sudo apt install ffmpeg`)

## Quick Start

### 1. Clone the Repository

```bash
git clone https://github.com/mattias800/snacka.git
cd snacka
```

### 2. Start the Development Database

```bash
docker compose -f docker-compose.dev.yml up -d
```

This starts a PostgreSQL instance for development.

### 3. Run the Application

The easiest way is using the dev script which starts the server and two test clients:

```bash
./dev-start.sh
```

Or run manually:

```bash
# Terminal 1: Start the server
cd src/Snacka.Server
dotnet run

# Terminal 2: Start the client
cd src/Snacka.Client
dotnet run -- --server http://localhost:5117
```

### 4. Development Login

For development, you can auto-login with test credentials:

```bash
dotnet run -- --server http://localhost:5117 --email alice@test.com --password test123
```

### 5. Stop the Database

```bash
docker compose -f docker-compose.dev.yml down
```

## Running Tests

```bash
# Run all tests
dotnet test

# Run specific test project
dotnet test tests/Snacka.Server.Tests
```

## Database

### Development Mode
The server uses SQLite by default in development mode. The database file is created automatically at `snacka.db`.

### Production Mode
For production, PostgreSQL is used. Configure the connection string in environment variables or `appsettings.json`.

### Entity Framework Migrations

```bash
# Create a new migration
cd src/Snacka.Server
dotnet ef migrations add MigrationName

# Apply migrations (happens automatically on startup)
dotnet ef database update
```

### Database Schema

- **Users**: User accounts with authentication
- **Communities**: Server/workspace management
- **Channels**: Text and voice channels
- **Messages**: Channel messages with attachments
- **DirectMessages**: Private messages between users
- **CommunityMembers**: Community membership and roles
- **VoiceParticipants**: Active voice channel tracking
- **CommunityInvites**: Pending invitations to communities

## Configuration

### Server Configuration

Key settings in `appsettings.json` or environment variables:

| Setting | Environment Variable | Description |
|---------|---------------------|-------------|
| `Jwt:SecretKey` | `Jwt__SecretKey` | JWT signing key (min 32 characters) |
| `ServerInfo:Name` | `ServerInfo__Name` | Server display name |
| `ServerInfo:AllowRegistration` | `ServerInfo__AllowRegistration` | Enable/disable registration |
| `ConnectionStrings:DefaultConnection` | `ConnectionStrings__DefaultConnection` | PostgreSQL connection |
| `Tenor:ApiKey` | `Tenor__ApiKey` | Optional Tenor API key for GIF picker |
| `UseSqlite` | `UseSqlite` | Use SQLite instead of PostgreSQL |

### Client Configuration

The client stores settings in a platform-specific location:
- **Windows**: `%APPDATA%/Snacka/`
- **macOS**: `~/Library/Application Support/Snacka/`
- **Linux**: `~/.config/Snacka/`

## Building for Release

### Client

```bash
# Windows
dotnet publish src/Snacka.Client -c Release -r win-x64 --self-contained -o publish

# macOS (Apple Silicon)
dotnet publish src/Snacka.Client -c Release -r osx-arm64 --self-contained -o publish

# Linux
dotnet publish src/Snacka.Client -c Release -r linux-x64 --self-contained -o publish
```

### Server (Docker)

```bash
docker build -t snacka-server .
```

## CI/CD

The project uses GitHub Actions for CI/CD:

- **build-client.yml**: Builds client for all platforms, creates installers, publishes releases
- **build-server.yml**: Builds and pushes server Docker image to GitHub Container Registry

Releases are triggered by pushing a version tag:

```bash
git tag v0.1.12
git push origin v0.1.12
```

## Architecture

### Client Architecture

The client uses MVVM pattern with ReactiveUI:

- **Views** (`*.axaml`): XAML-based UI definitions
- **ViewModels**: Business logic and state management
- **Services**: API client, SignalR, WebRTC, audio/video devices
- **Controls**: Reusable UI components

### Server Architecture

The server follows a layered architecture:

- **Controllers**: REST API endpoints
- **Hubs**: SignalR real-time communication
- **Services**: Business logic
- **Data**: Entity Framework DbContext and models

### Real-time Communication

- **SignalR**: Used for messaging, presence, and WebRTC signaling
- **WebRTC**: Used for voice/video with SDP offer/answer exchange over SignalR

## Implementation Status

### Completed
- User authentication (JWT + refresh tokens)
- Direct messages with typing indicators
- Text channels with message editing/deletion
- Voice channels with WebRTC
- Role-based permissions (Owner, Admin, Member)
- GIF picker with Tenor integration
- File attachments
- Cross-platform client
- Auto-updates (Windows)

### In Progress
- Webcam streaming
- Screen sharing
- Community invites UI

See [PLAN.md](PLAN.md) for the complete roadmap.

## Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

### Code Style

- Follow C# naming conventions
- Use meaningful commit messages
- Add tests for new features
- Update documentation as needed
