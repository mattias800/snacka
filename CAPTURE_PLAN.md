# Screen Capture & Game Streaming Plan

This document outlines the cross-platform screen capture strategy and future game streaming optimizations for Snacka.

---

## Current State

### macOS ✅
- **Video**: SnackaCapture (Swift) with ScreenCaptureKit on macOS 13+, ffmpeg fallback on older
- **Audio**: System audio via ScreenCaptureKit (app-specific filtering supported)
- **Encoding**: Software H.264 via ffmpeg (NV12 input from native capture)
- **Latency**: ~80-120ms end-to-end

### Windows ✅
- **Video**: SnackaCaptureWindows (Desktop Duplication API) on Windows 10+, ffmpeg fallback on older
- **Audio**: System audio via WASAPI loopback
- **Encoding**: Software H.264 via ffmpeg (NV12 input from native capture)
- **Latency**: ~80-120ms end-to-end

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
│                        Snacka.Client                           │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │                     WebRtcService                          │  │
│  │                                                            │  │
│  │  ┌─────────────────────────────────────────────────────┐  │  │
│  │  │              Platform Capture Provider               │  │  │
│  │  │                                                      │  │  │
│  │  │   if (macOS >= 13)     → SnackaCapture (Swift)     │  │  │
│  │  │   else if (macOS)      → ffmpeg + avfoundation      │  │  │
│  │  │   if (Windows >= 10)   → SnackaCapture (C++)       │  │  │
│  │  │   else if (Windows)    → ffmpeg + gdigrab           │  │  │
│  │  │   if (Linux + PipeWire)→ SnackaCapture (C/Rust)    │  │  │
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

### 1.1 macOS - SnackaCapture (Swift) ✅ COMPLETE

**Status**: Implemented and tested

**Location**: `src/SnackaCapture/`

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
SnackaCapture list --json

# Capture display
SnackaCapture capture --display 0 --width 1920 --height 1080 --fps 30 --audio

# Capture specific application with its audio
SnackaCapture capture --app "com.spotify.client" --width 1920 --height 1080 --fps 30 --audio

# Capture window
SnackaCapture capture --window 12345 --width 1920 --height 1080 --fps 30 --audio
```

**Integration Tasks**:
- [x] Update WebRtcService to detect macOS 13+ and use SnackaCapture
- [x] Parse audio packets from stderr
- [x] Encode and transmit screen share audio (PT 112 for Opus screen audio)
- [x] Update ScreenCaptureService to use SnackaCapture for source listing

---

### 1.2 Windows - SnackaCaptureWindows (C++) ✅ COMPLETE

**Status**: Fully implemented and integrated

**Location**: `src/SnackaCaptureWindows/`

**Technology**: DXGI Desktop Duplication API + WASAPI loopback

**Features** (implemented in native tool):
- [x] Display capture (Desktop Duplication API with D3D11)
- [x] Window capture
- [x] System audio capture (WASAPI loopback)
- [x] NV12 video output to stdout (GPU color conversion)
- [x] PCM audio output with packet headers
- [x] JSON source listing
- [x] ScreenCaptureService integration for source listing

**Integration Tasks**:
- [x] SnackaCaptureWindows CLI tool complete
- [x] ScreenCaptureService uses SnackaCaptureWindows for source listing
- [x] WebRtcService uses SnackaCaptureWindows for capture on Windows 10+
- [x] Audio packets parsed from stderr (same format as macOS)

**CLI Interface** (implemented):
```bash
SnackaCaptureWindows.exe list --json
SnackaCaptureWindows.exe --display 0 --width 1920 --height 1080 --fps 30 --audio
SnackaCaptureWindows.exe --window 12345678 --audio
```

---

### 1.3 Linux - SnackaCapture (C or Rust)

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
snacka-capture list --json
snacka-capture capture --display 0 --width 1920 --height 1080 --fps 30 --audio
```

---

## Phase 2: Hardware Encoding ✅ IMPLEMENTED (via FFmpeg)

### Current Implementation
Hardware encoding is implemented via FFmpeg with automatic encoder detection.

### Current Encoding Pipeline
```
Native Capture (NV12) → ffmpeg (hardware encoder) → H.264 NAL units → WebRTC
                        ~5-15ms (hardware)
```

### Implemented Hardware Encoders

| Platform | Encoder | Status |
|----------|---------|--------|
| macOS | h264_videotoolbox | ✅ Auto-enabled |
| Windows | h264_nvenc (NVIDIA) | ✅ Auto-detect |
| Windows | h264_amf (AMD) | ✅ Auto-detect |
| Windows | h264_qsv (Intel) | ✅ Auto-detect |
| Linux | h264_vaapi | ✅ Auto-detect |
| All | libx264 (software) | ✅ Fallback |

### Encoder Settings (Low-Latency Optimized)
- **No B-frames** (`-bf 0`) - Critical for low latency
- **Baseline profile** - No B-frame support needed
- **Realtime mode** - Prioritize speed over quality
- **Fast keyframes** - 10-60 frames for quick stream start
- **Small buffer** - Reduce buffering delay

### Future Optimization: Direct Hardware Encoding

For even lower latency (~2-5ms), bypass FFmpeg entirely:

**macOS**: Modify SnackaCapture to output H.264 directly via VTCompressionSession
**Windows**: Modify SnackaCaptureWindows to output H.264 via Media Foundation
**Linux**: Use VA-API or NVENC directly

This would eliminate the ffmpeg process overhead and enable zero-copy encoding from GPU capture surfaces.

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
1. ✅ macOS SnackaCapture with ScreenCaptureKit
2. ✅ Integrate SnackaCapture with WebRtcService
3. ✅ Screen share audio transmission (PT 112, separate Opus decoder in UserAudioMixer)

### Medium Priority
4. ✅ Windows SnackaCaptureWindows with Desktop Duplication + WASAPI
5. ⬜ Linux SnackaCapture with PipeWire
6. ✅ Hardware encoding via ffmpeg (VideoToolbox, NVENC, AMF, QSV, VA-API)
7. ✅ GPU video rendering (Metal on macOS, OpenGL on Windows/Linux)
8. ⬜ Hardware H.264 decoding on Windows (SnackaWindowsRenderer.dll - optional optimization)
9. ⬜ Hardware H.264 decoding on Linux (libSnackaLinuxRenderer.so - optional optimization)

### Lower Priority (Game Streaming)
10. ⬜ WebRTC latency tuning
11. ⬜ SRT transport option
12. ⬜ Input forwarding
13. ⬜ Game streaming UI/UX

---

## File Structure

```
src/
├── SnackaCapture/              # macOS (Swift) ✅
│   ├── Package.swift
│   └── Sources/
│       └── SnackaCapture/
│           ├── SnackaCaptureApp.swift
│           ├── ScreenCapturer.swift
│           ├── SourceLister.swift
│           └── Models.swift
│
├── SnackaCaptureWindows/       # Windows (C++) ✅
│   ├── CMakeLists.txt
│   └── src/
│       ├── main.cpp              # CLI entry point
│       ├── DisplayCapturer.cpp/h # Desktop Duplication API
│       ├── WindowCapturer.cpp/h  # Window capture
│       ├── AudioCapturer.cpp/h   # WASAPI loopback
│       ├── ColorConverter.cpp/h  # GPU color conversion
│       ├── SourceLister.cpp/h    # JSON source listing
│       └── Protocol.h            # Audio packet protocol
│
├── SnackaCapture.Linux/        # Linux (C or Rust) - Future
│   └── ...
│
└── Snacka.Client/
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
- [x] macOS: SnackaCapture captures display with audio
- [x] macOS: SnackaCapture captures specific app with its audio only
- [x] macOS: Audio transmitted alongside screen share video
- [x] macOS: Fallback to ffmpeg on macOS < 13
- [x] Windows: SnackaCaptureWindows captures display with Desktop Duplication API
- [x] Windows: SnackaCaptureWindows captures windows
- [x] Windows: SnackaCaptureWindows captures WASAPI audio
- [x] Windows: ScreenCaptureService uses SnackaCaptureWindows for source listing
- [x] Windows: WebRtcService integrated with SnackaCaptureWindows for capture
- [x] Windows: Audio transmitted alongside screen share video
- [x] Windows: Fallback to ffmpeg on Windows < 10 1803
- [ ] Linux: Capture with PipeWire audio

### Phase 2: Hardware Encoding
- [x] macOS: VideoToolbox encoding works (h264_videotoolbox via ffmpeg)
- [x] Windows: NVENC/AMF/QSV auto-detection works (h264_nvenc, h264_amf, h264_qsv via ffmpeg)
- [x] Linux: VA-API encoding works (h264_vaapi via ffmpeg)
- [x] Software fallback (libx264) when hardware unavailable
- [x] Low-latency settings: no B-frames, fast keyframe interval, realtime mode
- [ ] Direct hardware encoding (bypass ffmpeg) - not implemented

### Phase 2b: GPU Video Rendering
- [x] macOS: Metal rendering (MetalVideoRenderer)
- [x] Windows: OpenGL rendering (OpenGLVideoRenderer via Silk.NET)
- [x] Linux: OpenGL rendering (OpenGLVideoRenderer via Silk.NET)
- [x] NV12 → RGB conversion on GPU (all platforms)
- [x] Automatic fallback to software rendering if GPU fails

### Phase 2c: Hardware H.264 Decoding (Future - Zero-Copy)
- [x] macOS: VideoToolbox decoder (SnackaMetalRenderer - fully implemented)
- [ ] Windows: Media Foundation decoder (SnackaWindowsRenderer.dll - stub only)
- [ ] Linux: VA-API decoder (libSnackaLinuxRenderer.so - stub only)

Note: Hardware decoding would allow H.264 → GPU texture without CPU copy.
Currently, ffmpeg decodes to NV12 (CPU) → GPU upload → render.

### Phase 3: Low-Latency Transport
- [ ] WebRTC tuned for lower latency
- [ ] Optional SRT transport works
- [ ] End-to-end latency < 100ms

### Phase 4: Game Streaming
- [ ] End-to-end latency < 50ms
- [ ] Input forwarding works
- [ ] Playable for action games
