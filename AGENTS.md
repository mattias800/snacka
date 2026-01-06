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

- Use SIPSorcery library for WebRTC implementation
- SignalR handles signaling between peers
- Handle ICE candidate exchange properly
- Support STUN/TURN servers for NAT traversal

### SFU Architecture (Implemented)

The project uses a Selective Forwarding Unit (SFU) architecture for voice/video:

- **Server**: Each client connects to the server via a single WebRTC connection
- **Forwarding**: Server receives RTP packets and forwards them to other participants using `SendRtpRaw()` for minimal latency
- **Scalability**: N users need N connections (vs N*(N-1)/2 for P2P mesh)

Key server components:
- `SfuSession` - Manages one client's WebRTC connection
- `SfuChannelManager` - Coordinates media forwarding per voice channel
- `SfuService` - Top-level service managing all channels

Key client components:
- `WebRtcService._serverConnection` - Single connection to SFU server
- SignalR events: `SfuOfferReceived`, `SfuIceCandidateReceived`

### FFmpeg Video Encoding/Decoding

Video is encoded/decoded using FFmpeg as a subprocess (required on macOS where native bindings don't work):

**Encoder** (`FfmpegProcessEncoder`):
- Input: Raw BGR24 frames from OpenCV camera capture
- Output: H264 Annex B NAL units
- Low-latency flags: `-fflags nobuffer -flags low_delay -flush_packets 1`
- Codec settings: `-preset ultrafast -tune zerolatency`

**Decoder** (`FfmpegProcessDecoder`):
- Input: H264 Annex B stream
- Output: Raw RGB24 frames for display
- Low-latency flags: `-fflags nobuffer -flags low_delay -probesize 32 -analyzeduration 0`
- Aspect ratio preservation with `scale=W:H:force_original_aspect_ratio=decrease,pad`

**Important**: NAL units are emitted immediately as they complete (not waiting for frame boundaries) to minimize latency

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
- ✅ User authentication (register, login, JWT)
- ✅ SignalR hub for real-time communication
- ✅ Text channels with message history
- ✅ Voice channels with SFU architecture
- ✅ Webcam streaming with low-latency H264
- ✅ Audio device selection in settings
- ✅ Direct messages (DMs) with inline view

### In Progress / Next
- Screen sharing: Currently toggle mode (camera OR screen). Phase 2: camera AND screen as separate streams
- Testing and optimization
- UI polish

## Screen Sharing Architecture

### Current Implementation (Phase 1 - Toggle Mode)
- Screen share uses FFmpeg avfoundation to capture "Capture screen 0"
- Resolution: 1920x1080 @ 30fps (suitable for game streaming)
- Uses the same video track as camera (toggle between them)
- Limitation: Can't share camera and screen simultaneously

### Planned (Phase 2 - Dual Streams)
For simultaneous camera and screen share, each user needs two video boxes in the grid:
1. **Separate RTP streams**: Camera and screen need different SSRCs
2. **SFU changes**: Server must forward both streams separately
3. **UI changes**: Display two tiles per user if both are active
4. **Signaling**: Signal which stream is camera vs screen

Requirements for game streaming:
- Support 4K resolution (configurable)
- 60fps for smooth gameplay
- Low-latency encoding (ultrafast preset)

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

3. **Do NOT call SetAudioSourceFormat() on a running audio source** - Calling `SetAudioSourceFormat()` after `StartAudio()` will stop/reset the audio capture and no samples will be received. If you need to negotiate formats with WebRTC, set the format once before starting and don't change it:
   ```csharp
   // WRONG - this breaks audio capture:
   await audioSource.StartAudio();
   // ... later in OnAudioFormatsNegotiated:
   audioSource.SetAudioSourceFormat(negotiatedFormat); // Breaks capture!

   // CORRECT - set format once before starting:
   audioSource.SetAudioSourceFormat(format);
   await audioSource.StartAudio();
   // Don't call SetAudioSourceFormat again!
   ```

4. **Empty string for device name doesn't work on macOS** - You must specify an actual device name. Use `SDL2Helper.GetAudioRecordingDevices()` to get the list of available devices.

## When You Make Mistakes

If a user corrects you on something, update this file with the correction so future agents don't make the same mistake.
