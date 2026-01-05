# Agent Guidelines for Miscord

This document provides guidelines for AI agents working on the Miscord project.

## Project Overview

Miscord is a self-hosted Discord alternative built with C# and .NET. The project consists of:
- **Miscord.Server**: ASP.NET Core server application
- **Miscord.Client**: Avalonia UI desktop client (✅ basic structure with App, MainWindow)
- **Miscord.Shared**: Shared models and interfaces (✅ 7 database entities defined)
- **Miscord.WebRTC**: WebRTC/media handling library (planned)

## Core Requirements

### Features to Implement
1. User Accounts - Secure user authentication and account management
2. Direct Messages - Private messaging between users
3. Text Channels - Organized text-based communication
4. Voice Channels - Real-time voice communication
5. Webcam Streaming - Share camera in voice channels or private calls
6. Screen Sharing - Share screen with support for capture devices (e.g., Elgato 4K)

### Technology Stack
- **Language**: C# (.NET 9.0 - Latest stable LTS release)
- **Server**: ASP.NET Core 9
- **Desktop Client**: Avalonia UI 11.1.3 (cross-platform XAML framework for Windows, macOS, Linux)
- **Real-time Communication**: WebRTC with SipSorcery
- **Signaling**: SignalR for WebSocket-based signaling
- **Database**: Entity Framework Core with SQL Server or PostgreSQL
- **Testing**: MSTest with latest SDK

## Code Conventions

### C# Style
- Use arrow function syntax for all methods where possible
- Always use explicit type annotations (never use `dynamic` or untyped variables)
- Prefer interfaces over classes for abstractions
- All functions should have explicit return types
- One-liner methods should be expression-bodied

Example:
```csharp
public interface IUserService
{
    Task<User> GetUserByIdAsync(Guid userId);
}

public class UserService : IUserService
{
    private readonly IUserRepository _repository;
    
    public UserService(IUserRepository repository) => _repository = repository;
    
    public Task<User> GetUserByIdAsync(Guid userId) => _repository.FindByIdAsync(userId);
}
```

### Testing
- All features must have unit tests
- Test coverage should be verified automatically
- Integration tests for critical workflows
- No manual testing steps - everything must be automated

## UI Framework: Avalonia

Avalonia UI is a mature, cross-platform XAML framework that allows building desktop applications for Windows, macOS, and Linux from a single codebase.

### Why Avalonia for Miscord?
- **Cross-platform native performance** for all major desktop platforms
- **XAML-based** - familiar for developers with WPF background
- **Production-ready** with strong community and commercial backing (used by JetBrains in Rider)
- **Excellent for real-time communication UIs** with reactive programming support
- **All latest versions** - Built on .NET 9 with latest Avalonia 11.1.3

### Client Project Structure
- `Miscord.Client/` - Main executable with Avalonia UI
- `Views/` - XAML view files and code-behind
- `App.axaml` - Application definition
- `Program.cs` - Entry point with desktop platform detection

## Common Pitfalls to Avoid

1. **Never use `dynamic` type** - Always use proper type annotations
2. **Don't skip tests** - Write tests as you implement features
3. **Don't commit without testing** - Run `dotnet test` before committing
4. **Don't break the build** - Run `dotnet build` to verify compilation

## WebRTC Implementation Notes

- Use SipSorcery library for WebRTC implementation
- SignalR handles signaling between peers
- Support both peer-to-peer and server-mediated connections
- Handle ICE candidate exchange properly
- Support STUN/TURN servers for NAT traversal

## Database Conventions

- Use Entity Framework Core migrations
- Support both SQL Server and PostgreSQL
- Use proper foreign key relationships
- Index frequently queried columns
- Use soft deletes where appropriate

## Security

- Store passwords using bcrypt or similar
- Use JWT tokens for authentication
- Validate all user input
- Use HTTPS for all communications
- Sanitize output to prevent XSS

## Project Commands

```bash
# Build the solution
dotnet build

# Run tests
dotnet test

# Run the server (development)
dotnet run --project src/Miscord.Server

# Create a migration
dotnet ef migrations add MigrationName --project src/Miscord.Server
```

## Development Startup

Use the `dev-start.sh` script to start the server and two test clients with auto-login:

```bash
./dev-start.sh
```

This script:
1. Kills any existing Miscord processes
2. Builds the server and client projects
3. Starts the server on `http://localhost:5117`
4. Starts two client instances with auto-login:
   - **Alice** (`alice@test.com`) - typically the community owner
   - **Bob** (`bob@test.com`) - typically a regular member
5. Sets up proper cleanup on Ctrl+C

### Client CLI Arguments

The client supports the following CLI arguments for development/testing:

```bash
dotnet run --project src/Miscord.Client -- \
    --server "http://localhost:5117" \
    --email "user@test.com" \
    --password "password123" \
    --title "Miscord - Username" \
    --profile profilename
```

| Argument | Description |
|----------|-------------|
| `--server` | Server URL to auto-connect to |
| `--email` | Email for auto-login |
| `--password` | Password for auto-login |
| `--title` | Window title (useful for distinguishing multiple clients) |
| `--profile` | Profile name for storing separate settings |

## Project Status

### Completed
- ✅ Project structure with 4 main libraries (Server, Client, Shared, WebRTC)
- ✅ Avalonia UI 11.1.3 integrated with ReactiveUI
- ✅ Database models (7 entities) with EF Core DbContext
- ✅ Build pipeline working (zero errors)
- ✅ Git repository on GitHub (private)
- ✅ PLAN.md with complete feature roadmap

### In Progress / Next
- Phase 1.2: User authentication (register, login, JWT)
- Phase 1.3: SignalR hub setup
- Phase 2: Messaging (DMs and channels)
- Phase 3: Voice/media (WebRTC, webcam, screen share)
- Phase 4: UI implementation
- Phase 5: Testing and optimization

## SDL2 and Audio on macOS

The project uses `SIPSorceryMedia.SDL2` for audio capture/playback. On macOS, there are specific requirements:

### SDL2 Library Loading

SDL2 is NOT bundled with the NuGet package. On macOS, it must be installed via Homebrew:
```bash
brew install sdl2
```

The library is located at `/opt/homebrew/lib/libSDL2.dylib` (Apple Silicon) or `/usr/local/lib/libSDL2.dylib` (Intel).

**Important:** You must use `NativeLibrary.SetDllImportResolver` to redirect P/Invoke calls to the Homebrew location. Simply setting `DYLD_LIBRARY_PATH` causes crashes with Avalonia UI.

```csharp
NativeLibrary.SetDllImportResolver(typeof(SDL2Helper).Assembly, (libraryName, assembly, searchPath) =>
{
    if (libraryName == "SDL2")
    {
        if (NativeLibrary.TryLoad("/opt/homebrew/lib/libSDL2.dylib", out var handle))
            return handle;
    }
    return IntPtr.Zero;
});
```

### SDL2 Audio Initialization

Before enumerating audio devices, you MUST call `SDL_Init(SDL_INIT_AUDIO)`:

```csharp
[DllImport("SDL2")]
private static extern int SDL_Init(uint flags);

const uint SDL_INIT_AUDIO = 0x00000010;

SDL_Init(SDL_INIT_AUDIO); // Call this before SDL2Helper.GetAudioRecordingDevices()
```

### SDL2AudioSource Requirements

When using `SDL2AudioSource` for audio capture:

1. **Set audio format BEFORE calling StartAudio()** - The audio source remains paused until a format is set:
   ```csharp
   var audioSource = new SDL2AudioSource(deviceName, audioEncoder);
   var formats = audioSource.GetAudioSourceFormats();
   audioSource.SetAudioSourceFormat(formats[0]); // Required!
   await audioSource.StartAudio();
   ```

2. Without setting the format, `IsAudioSourcePaused()` will return `true` and no samples will be received.

## When You Make Mistakes

If a user corrects you on something, update this file with the correction so future agents don't make the same mistake.
