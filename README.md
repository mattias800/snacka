# Miscord - Discord Clone

A self-hosted Discord alternative built with C# and .NET. Miscord allows users to run their own communication server with full control over data and infrastructure.

## Features

- **User Accounts**: Secure user authentication and account management
- **Direct Messages**: Private messaging between users
- **Text Channels**: Organized text-based communication
- **Voice Channels**: Real-time voice communication
- **Webcam Streaming**: Share your camera in voice channels or private calls
- **Screen Sharing**: Share your screen with support for capture devices (e.g., Elgato 4K)
- **Cross-platform Client**: Desktop application for Windows, macOS, and Linux
- **Self-hosted Server**: Deploy on your own infrastructure for complete control

## Technology Stack

- **Language**: C# (.NET 9.0)
- **Server**: ASP.NET Core 9
- **Desktop Client**: Avalonia UI 11.1.3 (cross-platform XAML framework)
- **Real-time Communication**: WebRTC with SipSorcery
- **Signaling**: SignalR for WebSocket-based signaling
- **Database**: Entity Framework Core with SQLite or PostgreSQL
- **Testing**: MSTest

## Project Structure

```
miscord-csharp/
├── src/
│   ├── Miscord.Server/          # ASP.NET Core server application
│   ├── Miscord.Client/          # Avalonia UI desktop client (Windows, macOS, Linux)
│   ├── Miscord.Shared/          # Shared models and interfaces
│   └── Miscord.WebRTC/          # WebRTC/media handling
├── tests/
│   ├── Miscord.Server.Tests/
│   └── Miscord.WebRTC.Tests/
├── AGENTS.md
├── .gitignore
├── .editorconfig
├── Miscord.sln
└── README.md
```

## Implementation Status

### Current Progress
- ✅ **Phase 1.1 - Database Setup (100%)**: All 7 entity models created with EF Core DbContext
  - User, MiscordServer, Channel, Message, DirectMessage, UserServer, VoiceParticipant
  - Migrations ready for deployment
  - Foreign key relationships configured
  - Unique constraints and indexes optimized
- ⏳ **Phase 1.2 - User Authentication (0%)**: Next to implement
- ⏳ **Phase 1.3 - SignalR Setup (0%)**: Planned after authentication
- ⏳ **Phases 2-5**: Messaging, voice/media, UI, testing

See [PLAN.md](PLAN.md) for complete implementation roadmap.

## Development

### Prerequisites
- .NET 9 SDK or later
- Visual Studio 2022 (recommended), Rider, or VS Code
- Git

### Building

```bash
cd /Users/mattias800/repos/miscord-csharp
dotnet build
```

### Running Tests

```bash
dotnet test
```

### Database

The project uses Entity Framework Core with support for SQL Server and PostgreSQL. Database schema includes:
- **Users**: User accounts with authentication
- **MiscordServers**: Server/workspace management
- **Channels**: Text and voice channels
- **Messages**: Channel messages
- **DirectMessages**: Private messages
- **UserServers**: Server membership and roles
- **VoiceParticipants**: Active voice channel tracking

## Code Style

This project follows C# coding standards with the following conventions:
- Arrow function syntax for all methods
- Explicit type annotations (no `any`)
- Interfaces preferred over types
- Proper testing with automated verification

## License

TBD

## Contributing

TBD
