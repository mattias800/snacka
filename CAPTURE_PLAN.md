# Screen Capture & Game Streaming Plan

This document outlines the cross-platform screen capture strategy and future game streaming optimizations for Miscord.

---

## Current State

### macOS
- **Video**: ffmpeg with avfoundation (`Capture screen N`)
- **Audio**: None (no system audio capture)
- **Encoding**: Software H.264 via ffmpeg
- **Latency**: ~100-150ms end-to-end

### Windows
- **Video**: ffmpeg with gdigrab
- **Audio**: None
- **Encoding**: Software H.264 via ffmpeg

### Linux
- **Video**: ffmpeg with x11grab
- **Audio**: None
- **Encoding**: Software H.264 via ffmpeg

---

## Phase 1: Native Capture with System Audio (Current Focus)

### Goal
Replace ffmpeg-based capture with native platform APIs that support system audio capture.

### Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        Miscord.Client                           │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │                     WebRtcService                          │  │
│  │                                                            │  │
│  │  ┌─────────────────────────────────────────────────────┐  │  │
│  │  │              Platform Capture Provider               │  │  │
│  │  │                                                      │  │  │
│  │  │   if (macOS >= 13)     → MiscordCapture (Swift)     │  │  │
│  │  │   else if (macOS)      → ffmpeg + avfoundation      │  │  │
│  │  │   if (Windows >= 10)   → MiscordCapture (C++)       │  │  │
│  │  │   else if (Windows)    → ffmpeg + gdigrab           │  │  │
│  │  │   if (Linux + PipeWire)→ MiscordCapture (C/Rust)    │  │  │
│  │  │   else if (Linux)      → ffmpeg + x11grab           │  │  │
│  │  │                                                      │  │  │
│  │  └─────────────────────────────────────────────────────┘  │  │
│  │                           │                                │  │
│  │                           ▼                                │  │
│  │  ┌─────────────────────────────────────────────────────┐  │  │
│  │  │                  Unified Interface                   │  │  │
│  │  │                                                      │  │  │
│  │  │  stdout: BGR24 raw video frames                     │  │  │
│  │  │  stderr: PCM audio packets (with headers)           │  │  │
│  │  │  JSON:   Source listing                             │  │  │
│  │  │                                                      │  │  │
│  │  └─────────────────────────────────────────────────────┘  │  │
│  └───────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

### 1.1 macOS - MiscordCapture (Swift) ✅ COMPLETE

**Status**: Implemented and tested

**Location**: `src/MiscordCapture/`

**Technology**: ScreenCaptureKit (macOS 13+)

**Features**:
- [x] Display capture
- [x] Window capture
- [x] Application capture
- [x] System audio capture (app-specific)
- [x] BGR24 video output to stdout
- [x] PCM audio output with packet headers
- [x] JSON source listing

**CLI Interface**:
```bash
# List sources
MiscordCapture list --json

# Capture display
MiscordCapture capture --display 0 --width 1920 --height 1080 --fps 30 --audio

# Capture specific application with its audio
MiscordCapture capture --app "com.spotify.client" --width 1920 --height 1080 --fps 30 --audio

# Capture window
MiscordCapture capture --window 12345 --width 1920 --height 1080 --fps 30 --audio
```

**Integration Tasks**:
- [ ] Update WebRtcService to detect macOS 13+ and use MiscordCapture
- [ ] Parse audio packets from stderr
- [ ] Encode and transmit screen share audio
- [ ] Update ScreenCaptureService to use MiscordCapture for source listing

---

### 1.2 Windows - MiscordCapture (C++ or C#)

**Status**: Not started

**Technology Options**:

| API | Pros | Cons |
|-----|------|------|
| Windows.Graphics.Capture | Modern, efficient, UWP | Requires Win10 1803+ |
| DXGI Desktop Duplication | Low-level, fast | Complex, no audio |
| BitBlt/GDI | Simple, universal | Slow, CPU-intensive |

**Recommended**: Windows.Graphics.Capture + WASAPI loopback

**Audio Capture**: WASAPI loopback capture is straightforward on Windows - it captures all system audio without special drivers.

**Implementation Options**:

1. **C++ CLI tool** (like macOS Swift version)
   - Pros: Native performance, direct API access
   - Cons: Separate build system, more complex

2. **C# with CsWin32** (P/Invoke source generator)
   - Pros: Same language as main app, could be integrated directly
   - Cons: Interop overhead (minimal)

3. **Rust CLI tool**
   - Pros: Cross-platform potential, memory safety
   - Cons: Another language in the stack

**Suggested CLI Interface** (same as macOS):
```bash
MiscordCapture.exe list --json
MiscordCapture.exe capture --display 0 --width 1920 --height 1080 --fps 30 --audio
```

---

### 1.3 Linux - MiscordCapture (C or Rust)

**Status**: Not started

**Technology Options**:

| API | Pros | Cons |
|-----|------|------|
| PipeWire | Modern, Wayland-native, has audio | Newer distros only |
| XDG Desktop Portal | Standard D-Bus API | Depends on compositor |
| X11 (XShm/XComposite) | Universal X11 support | No Wayland, no audio |

**Recommended**: PipeWire with XDG Desktop Portal fallback

**Audio Capture**: PipeWire provides unified audio/video capture - the same API captures both.

**Implementation Options**:

1. **C with libpipewire**
   - Pros: Direct API access, small binary
   - Cons: Manual memory management

2. **Rust with pipewire-rs**
   - Pros: Memory safety, good PipeWire bindings
   - Cons: Larger binary, another language

**Suggested CLI Interface** (same as macOS/Windows):
```bash
miscord-capture list --json
miscord-capture capture --display 0 --width 1920 --height 1080 --fps 30 --audio
```

---

## Phase 2: Hardware Encoding

### Goal
Replace software H.264 encoding with hardware encoders for lower latency and CPU usage.

### Current Encoding Pipeline
```
Raw BGR24 → ffmpeg (CPU) → H.264 NAL units → WebRTC
           ~20-50ms
```

### Target Encoding Pipeline
```
Raw frames → Hardware Encoder → H.264 NAL units → WebRTC
              ~2-5ms
```

### Platform-Specific Hardware Encoders

| Platform | API | Hardware |
|----------|-----|----------|
| macOS | VideoToolbox | Apple Silicon / Intel QSV |
| Windows | Media Foundation | NVENC, AMD AMF, Intel QSV |
| Linux | VAAPI / NVENC | Intel, AMD, NVIDIA |

### 2.1 macOS VideoToolbox Integration

**Optimization Path**:
1. ScreenCaptureKit provides IOSurface (GPU memory)
2. Pass IOSurface directly to VideoToolbox (no CPU copy!)
3. VideoToolbox outputs H.264 NAL units
4. Send to WebRTC

**Latency Savings**: ~30-50ms (eliminates BGR24 conversion + CPU encoding)

**Implementation**:
- Modify MiscordCapture to optionally output H.264 directly
- Add `--encode h264` flag
- Use VTCompressionSession with IOSurface input

### 2.2 Windows Hardware Encoding

**Options**:
- **NVENC** (NVIDIA): Lowest latency, best for gaming
- **AMD AMF**: Good performance on AMD GPUs
- **Intel Quick Sync**: Available on Intel iGPUs

**Implementation**:
- Use Media Foundation Transform (MFT) for unified API
- Or direct NVENC/AMF for lowest latency

### 2.3 Linux Hardware Encoding

**Options**:
- **VAAPI**: Intel and AMD (mesa)
- **NVENC**: NVIDIA (proprietary driver)

**Implementation**:
- FFmpeg with VAAPI/NVENC backend
- Or GStreamer with hardware plugins

---

## Phase 3: Low-Latency Transport

### Goal
Optimize WebRTC for gaming latency or provide alternative transports.

### Current Transport
- WebRTC with default settings
- ~50-100ms network latency
- Jitter buffer adds delay

### Optimization Options

#### 3.1 WebRTC Tuning
- Reduce jitter buffer size
- Disable FEC (Forward Error Correction) for lower latency
- Use CBR (Constant Bit Rate) instead of VBR
- Prioritize latency over quality in bitrate adaptation

#### 3.2 Alternative: SRT (Secure Reliable Transport)
- Designed for live streaming
- ~20-50ms latency achievable
- Better for point-to-point game streaming
- Used by OBS, vMix, etc.

#### 3.3 Alternative: RIST (Reliable Internet Stream Transport)
- Similar to SRT
- Better multi-path support
- Interoperable standard

### Suggested Approach
1. **Short-term**: Tune WebRTC for lower latency
2. **Medium-term**: Add SRT as alternative transport for game streaming mode
3. **Long-term**: Evaluate RIST or custom UDP protocol

---

## Phase 4: Game Streaming Mode

### Goal
Dedicated low-latency mode optimized for game streaming.

### Features
- [ ] Hardware encoding (Phase 2)
- [ ] Optimized transport (Phase 3)
- [ ] Controller input forwarding
- [ ] Mouse/keyboard input with low latency
- [ ] Adaptive bitrate based on network conditions
- [ ] Quality presets (Balanced, Performance, Quality)

### Target Latency Budget

| Component | Target | Notes |
|-----------|--------|-------|
| Capture | 8ms | 120fps capture |
| Encoding | 5ms | Hardware H.264/HEVC |
| Network | 20ms | Tuned WebRTC or SRT |
| Decode | 5ms | Hardware decode |
| Display | 8ms | 120Hz monitor |
| **Total** | **~46ms** | Playable for most games |

Compare to current: ~150-200ms (not playable for fast games)

---

## Implementation Priority

### High Priority (Current)
1. ✅ macOS MiscordCapture with ScreenCaptureKit
2. ⬜ Integrate MiscordCapture with WebRtcService
3. ⬜ Screen share audio transmission

### Medium Priority
4. ⬜ Windows MiscordCapture with WGC + WASAPI
5. ⬜ Linux MiscordCapture with PipeWire
6. ⬜ Hardware encoding (VideoToolbox first)

### Lower Priority (Game Streaming)
7. ⬜ WebRTC latency tuning
8. ⬜ SRT transport option
9. ⬜ Input forwarding
10. ⬜ Game streaming UI/UX

---

## File Structure

```
src/
├── MiscordCapture/              # macOS (Swift) ✅
│   ├── Package.swift
│   └── Sources/
│       └── MiscordCapture/
│           ├── MiscordCaptureApp.swift
│           ├── ScreenCapturer.swift
│           ├── SourceLister.swift
│           └── Models.swift
│
├── MiscordCapture.Windows/      # Windows (C++ or C#) - Future
│   └── ...
│
├── MiscordCapture.Linux/        # Linux (C or Rust) - Future
│   └── ...
│
└── Miscord.Client/
    └── Services/
        ├── IScreenCaptureService.cs
        ├── ScreenCaptureService.cs      # Platform detection, source listing
        ├── WebRtcService.cs             # Uses appropriate capture tool
        └── CaptureProviders/            # Future: abstraction layer
            ├── ICaptureProvider.cs
            ├── MacOSCaptureProvider.cs
            ├── WindowsCaptureProvider.cs
            └── LinuxCaptureProvider.cs
```

---

## Testing Checklist

### Phase 1: Native Capture
- [ ] macOS: MiscordCapture captures display with audio
- [ ] macOS: MiscordCapture captures specific app with its audio only
- [ ] macOS: Audio transmitted alongside screen share video
- [ ] macOS: Fallback to ffmpeg on macOS < 13
- [ ] Windows: Capture with WASAPI audio
- [ ] Linux: Capture with PipeWire audio

### Phase 2: Hardware Encoding
- [ ] macOS: VideoToolbox encoding works
- [ ] Latency reduced by 30-50ms
- [ ] CPU usage significantly lower

### Phase 3: Low-Latency Transport
- [ ] WebRTC tuned for lower latency
- [ ] Optional SRT transport works
- [ ] End-to-end latency < 100ms

### Phase 4: Game Streaming
- [ ] End-to-end latency < 50ms
- [ ] Input forwarding works
- [ ] Playable for action games
