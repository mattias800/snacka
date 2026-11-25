# Legacy Avalonia client notes

These notes describe the **old** Avalonia client and custom SFU (`src/Snacka.Client`, `src/Snacka.Server/Services/Sfu`).
They are being replaced per [ADR 0001](../adr/0001-client-architecture.md). They're kept here for anyone maintaining the old client until it is deleted.
Some statements were already outdated in Nov 2026: capture now goes through the native `SnackaCapture*` helpers, and dual camera + screen streams are implemented.

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

