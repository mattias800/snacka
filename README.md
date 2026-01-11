# Snacka - Discord Clone

A self-hosted Discord alternative built with C# and .NET. Snacka allows users to run their own communication server with full control over data and infrastructure.

## Features

- **User Accounts**: Secure user authentication with JWT tokens and refresh tokens
- **Direct Messages**: Private messaging between users with typing indicators
- **Text Channels**: Organized text-based communication with edit/delete support
- **Voice Channels**: Real-time voice communication with WebRTC
- **Webcam Streaming**: Share your camera in voice channels or private calls (in progress)
- **Screen Sharing**: Share your screen with support for capture devices (in progress)
- **GIF Picker**: Search and share GIFs via Tenor integration
- **File Attachments**: Share images, audio, and other files
- **Cross-platform Client**: Desktop application for Windows, macOS, and Linux
- **Self-hosted Server**: Deploy on your own infrastructure with Docker support

## Technology Stack

- **Language**: C# (.NET 9.0)
- **Server**: ASP.NET Core 9
- **Desktop Client**: Avalonia UI 11.1.3 (cross-platform XAML framework)
- **Real-time Communication**: WebRTC with SipSorcery
- **Signaling**: SignalR for WebSocket-based signaling
- **Database**: Entity Framework Core with SQLite or PostgreSQL
- **Audio**: SDL2 for audio capture and playback
- **Testing**: MSTest

## Project Structure

```
miscord-csharp/
├── src/
│   ├── Snacka.Server/          # ASP.NET Core server application
│   ├── Snacka.Client/          # Avalonia UI desktop client (Windows, macOS, Linux)
│   ├── Snacka.Shared/          # Shared models and interfaces
│   ├── Snacka.WebRTC/          # WebRTC/media handling
│   ├── SnackaCapture/          # Screen/window capture utilities
│   └── SnackaMetalRenderer/    # Metal-based rendering (macOS)
├── tests/
│   ├── Snacka.Server.Tests/
│   └── Snacka.WebRTC.Tests/
├── tools/                      # Build and development tools
├── docs/
│   └── DEPLOY.md               # Deployment guide
├── AGENTS.md
├── PLAN.md
├── Snacka.sln
├── Dockerfile
├── docker-compose.yml
└── README.md
```

## Implementation Status

### Completed Features
- ✅ **Database Models**: All 7 entity models with EF Core DbContext
- ✅ **User Authentication**: Register, login, JWT tokens, refresh tokens
- ✅ **SignalR Hub**: Real-time messaging, presence tracking, WebRTC signaling
- ✅ **Direct Messages**: Send, receive, edit, delete, typing indicators
- ✅ **Text Channels**: Create, edit, delete channels and messages
- ✅ **Voice Channels**: Join/leave with full WebRTC audio support
- ✅ **Role-based Permissions**: Owner, Admin, Member roles
- ✅ **Ownership Transfer**: Transfer server ownership between users
- ✅ **Discord-like UI**: Server list, channels, chat, member list
- ✅ **Audio Device Selection**: Input/output device configuration
- ✅ **Audio Controls**: Input gain (0-300%), noise gate
- ✅ **75+ Automated Tests**

### In Progress
- ⏳ **Webcam Streaming**: Video track integration with WebRTC
- ⏳ **Screen Sharing**: Platform-specific screen capture
- ⏳ **STUN/TURN Configuration**: NAT traversal for voice calls

See [PLAN.md](PLAN.md) for the complete implementation roadmap.

## Quick Start

### Prerequisites
- .NET 9 SDK or later
- Git
- Native dependencies for client (SDL2, VLC for audio playback) - see [docs/DEPLOY.md](docs/DEPLOY.md)

### Development

The easiest way to run Snacka for development:

```bash
# Start server and two test clients (Alice and Bob)
./dev-start.sh
```

Or manually:

```bash
# Start server
cd src/Snacka.Server
dotnet run

# Start client (in another terminal)
cd src/Snacka.Client
dotnet run -- --server http://localhost:5117
```

### Docker Deployment

```bash
# Configure environment
cp .env.example .env
# Edit .env and set JWT_SECRET_KEY

# Start server
docker-compose up -d
```

See [docs/DEPLOY.md](docs/DEPLOY.md) for complete deployment instructions including production setup with PostgreSQL and nginx.

### Running Tests

```bash
dotnet test
```

## Database

The project uses Entity Framework Core with support for SQLite and PostgreSQL. Database schema includes:
- **Users**: User accounts with authentication
- **SnackaServers**: Server/workspace management
- **Channels**: Text and voice channels
- **Messages**: Channel messages
- **DirectMessages**: Private messages
- **UserServers**: Server membership and roles
- **VoiceParticipants**: Active voice channel tracking

## Configuration

Key configuration options in `appsettings.json`:

| Setting | Description |
|---------|-------------|
| `Jwt:SecretKey` | JWT signing key (min 32 characters) |
| `ServerInfo:Name` | Server display name |
| `ServerInfo:AllowRegistration` | Enable/disable new user registration |
| `UseSqlite` | Use SQLite (true) or PostgreSQL (false) |
| `Tenor:ApiKey` | Optional Tenor API key for GIF picker |

## License

TBD

## Contributing

TBD
