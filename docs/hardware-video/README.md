# Hardware Video Decoding Architecture

This document describes the cross-platform hardware video decoding system for Miscord.

## Overview

Miscord uses hardware-accelerated video decoding for screen share and camera streams. The system provides a zero-copy pipeline from H264-encoded WebRTC frames directly to GPU rendering, bypassing CPU-based decoding entirely.

```
WebRTC H264 NAL Units
        │
        ▼
┌─────────────────────────────────────────────────────────────┐
│              IHardwareVideoDecoder Interface                │
│   Initialize(width, height, sps, pps)                       │
│   DecodeAndRender(nalUnit, isKeyframe)                      │
│   NativeViewHandle (for Avalonia embedding)                 │
└─────────────────────────────────────────────────────────────┘
        │
        ├──────────────────┬───────────────────┐
        ▼                  ▼                   ▼
┌───────────────┐  ┌───────────────┐  ┌───────────────┐
│    macOS      │  │   Windows     │  │    Linux      │
│  VideoToolbox │  │ MediaFoundation│ │   VA-API      │
│  + Metal      │  │ + D3D11       │  │ + EGL/OpenGL  │
└───────────────┘  └───────────────┘  └───────────────┘
        │                  │                   │
        ▼                  ▼                   ▼
   NSView/MTKView      HWND/D3D11         X11 Window
   (overlay window)   (overlay window)   (overlay window)
```

## Implementation Status

| Platform | Status | Decoder | Renderer | Documentation |
|----------|--------|---------|----------|---------------|
| macOS | ✅ Complete | VideoToolbox | Metal | Reference implementation |
| Windows | 📝 Documented | Media Foundation | D3D11 | [WINDOWS_IMPLEMENTATION.md](WINDOWS_IMPLEMENTATION.md) |
| Linux | 📝 Documented | VA-API | EGL/OpenGL | [LINUX_IMPLEMENTATION.md](LINUX_IMPLEMENTATION.md) |

## Architecture Components

### 1. C# Interface (`IHardwareVideoDecoder`)

Location: `src/Miscord.Client/Services/HardwareVideo/IHardwareVideoDecoder.cs`

The interface that all platform implementations must follow:

```csharp
public interface IHardwareVideoDecoder : IDisposable
{
    bool IsInitialized { get; }
    (int Width, int Height) VideoDimensions { get; }
    bool Initialize(int width, int height, ReadOnlySpan<byte> sps, ReadOnlySpan<byte> pps);
    bool DecodeAndRender(ReadOnlySpan<byte> nalUnit, bool isKeyframe);
    nint NativeViewHandle { get; }
    void SetDisplaySize(int width, int height);
}
```

### 2. Platform Implementations

| File | Platform | Native Library |
|------|----------|----------------|
| `VideoToolboxDecoder.cs` | macOS | `libMiscordMetalRenderer.dylib` |
| `MediaFoundationDecoder.cs` | Windows | `MiscordWindowsRenderer.dll` |
| `VaapiDecoder.cs` | Linux | `libMiscordLinuxRenderer.so` |

### 3. Factory

`HardwareVideoDecoderFactory.Create()` automatically selects the correct implementation based on the current OS.

### 4. Avalonia Integration

The `HardwareVideoViewHost` control in `src/Miscord.Client/Controls/HardwareVideoView.cs` embeds the native view using Avalonia's `NativeControlHost`.

## Key Design Decisions

### Overlay Window Pattern

All platforms use an overlay window approach rather than direct embedding. This solves compositor issues where Avalonia's rendering pipeline conflicts with native GPU surfaces.

- **macOS**: Child `NSWindow` with `ignoresMouseEvents = true`
- **Windows**: Child HWND with `WS_EX_TRANSPARENT`
- **Linux**: Override-redirect X11 window with XFixes input shape

### Zero-Copy Pipeline

Decoded frames stay in GPU memory throughout:
1. H264 NAL → Hardware decoder produces GPU texture
2. Shader converts NV12/YUV → RGB directly on GPU
3. Compositor presents to display

No CPU copies of video frame data.

### SPS/PPS Handling

H264 requires Sequence Parameter Set (SPS) and Picture Parameter Set (PPS) for initialization. These are extracted from keyframes before decode can begin:

```csharp
// NAL types in H264
const int NAL_SPS = 7;
const int NAL_PPS = 8;
const int NAL_IDR = 5;  // Keyframe
const int NAL_SLICE = 1; // P-frame
```

## Native Library C API

All platforms use the same C API pattern for P/Invoke:

```c
// Create/destroy
Handle decoder_create();
void decoder_destroy(Handle decoder);

// Initialize with H264 parameters
bool decoder_initialize(Handle decoder, int w, int h,
                        uint8_t* sps, int spsLen,
                        uint8_t* pps, int ppsLen);

// Decode and render
bool decoder_decode_and_render(Handle decoder,
                               uint8_t* nal, int nalLen,
                               bool isKeyframe);

// Native view for embedding
void* decoder_get_view(Handle decoder);

// Display sizing
void decoder_set_display_size(Handle decoder, int w, int h);

// Availability check
bool decoder_is_available();
```

## Building Native Libraries

### macOS (VideoToolbox + Metal)

```bash
cd src/MiscordMetalRenderer
swift build -c release
# Output: .build/release/libMiscordMetalRenderer.dylib
```

### Windows (MediaFoundation + D3D11)

See [WINDOWS_IMPLEMENTATION.md](WINDOWS_IMPLEMENTATION.md) for Visual Studio/CMake setup.

### Linux (VA-API + EGL)

See [LINUX_IMPLEMENTATION.md](LINUX_IMPLEMENTATION.md) for CMake setup and dependencies.

## Integration with WebRTC

The `WebRtcService` processes incoming video frames:

1. Parses Annex B stream to extract NAL units
2. Caches SPS/PPS from keyframes
3. Initializes hardware decoder when parameters are available
4. Feeds NAL units to `DecodeAndRender()`
5. Fires `HardwareDecoderReady` event for UI to embed the native view

See `TryProcessWithHardwareDecoder()` in `WebRtcService.cs`.

## Fallback Behavior

If hardware decoding is unavailable:
- `HardwareVideoDecoderFactory.Create()` returns `null`
- `WebRtcService` falls back to `FfmpegProcessDecoder`
- Software-decoded frames are rendered via Avalonia `Image` control

## Testing

Each platform implementation should verify:

1. **Decoder creation** - `TryCreate()` returns non-null
2. **Initialization** - SPS/PPS accepted, context created
3. **Keyframe decode** - First IDR frame decodes and displays
4. **P-frame decode** - Subsequent frames decode correctly
5. **Performance** - 30+ FPS at 1080p with low CPU usage
6. **Overlay positioning** - Window tracks parent correctly
7. **Memory** - No leaks (especially texture/buffer leaks)

## Files Reference

```
src/Miscord.Client/Services/HardwareVideo/
├── IHardwareVideoDecoder.cs      # Interface + factory
├── VideoToolboxDecoder.cs        # macOS implementation ✅
├── MediaFoundationDecoder.cs     # Windows stub (P/Invoke ready)
└── VaapiDecoder.cs               # Linux stub (P/Invoke ready)

src/Miscord.Client/Controls/
└── HardwareVideoView.cs          # Avalonia NativeControlHost wrapper

src/MiscordMetalRenderer/         # macOS native library (Swift)
├── Package.swift
└── Sources/MiscordMetalRenderer/
    ├── VideoToolboxDecoder.swift
    ├── VideoToolboxDecoderCApi.swift
    └── ... (Metal renderer)

docs/hardware-video/
├── README.md                     # This file
├── WINDOWS_IMPLEMENTATION.md     # Windows guide
└── LINUX_IMPLEMENTATION.md       # Linux guide
```

## For Implementing Agents

If you're an AI agent implementing one of the platform decoders:

1. Read the platform-specific guide (WINDOWS_IMPLEMENTATION.md or LINUX_IMPLEMENTATION.md)
2. Create the native library project following the C API specification
3. The C# P/Invoke wrapper is already complete - just build the native library
4. Test with the existing Miscord client
5. The overlay window pattern is critical - don't try direct NativeControlHost embedding

The macOS implementation in `src/MiscordMetalRenderer/` serves as the reference implementation.
