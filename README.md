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

- **Language**: C# (.NET 8+)
- **Server**: ASP.NET Core
- **Real-time Communication**: WebRTC with SipSorcery
- **Signaling**: SignalR for WebSocket-based signaling
- **Database**: SQL Server or PostgreSQL
- **Client UI**: WPF or Electron-based

## Project Structure

```
miscord-csharp/
├── src/
│   ├── Miscord.Server/          # ASP.NET Core server application
│   ├── Miscord.Client/          # Desktop client application
│   ├── Miscord.Shared/          # Shared models and interfaces
│   └── Miscord.WebRTC/          # WebRTC/media handling
├── tests/
│   ├── Miscord.Server.Tests/
│   ├── Miscord.Client.Tests/
│   └── Miscord.WebRTC.Tests/
├── docs/
├── .gitignore
├── .editorconfig
├── Miscord.sln
└── README.md
```

## Development

### Prerequisites
- .NET 8 SDK or later
- Visual Studio 2022 (recommended) or Rider
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
