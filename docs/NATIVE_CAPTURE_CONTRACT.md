# Native Capture Tool Contract

This document defines the **mandatory interface** that all native capture tools (macOS, Windows, Linux) must follow. The C# client depends on this exact format.

## Critical Requirements

### 1. H.264 Output Format: AVCC (NOT Annex-B)

**All encoded H.264 output MUST be in AVCC format:**

```
[4-byte big-endian length][NAL unit data][4-byte big-endian length][NAL unit data]...
```

**NOT Annex-B format (which uses 00 00 00 01 start codes).**

The C# client (`CameraManager.cs`) expects AVCC format and converts to Annex-B internally for WebRTC transmission.

### 2. SPS/PPS on Keyframes

On every keyframe, the SPS and PPS NAL units MUST be included **before** the IDR slice, each with their own 4-byte length prefix:

```
[4-byte length][SPS NAL][4-byte length][PPS NAL][4-byte length][IDR slice]
```

### 3. NAL Unit Types

The following NAL unit types must be handled:
- Type 7 (SPS) - Sequence Parameter Set
- Type 8 (PPS) - Picture Parameter Set
- Type 5 (IDR) - Keyframe slice
- Type 1 (Non-IDR) - P-frame slice

### 4. Output Streams

| Stream | Content | Format |
|--------|---------|--------|
| **stdout** | H.264 video | AVCC format (4-byte length prefixed NAL units) |
| **stderr** | Audio + Logs | Binary audio packets (MCAP header) + text logs |

### 5. Audio Packet Format (stderr)

```c
struct AudioPacketHeader {  // 24 bytes, all big-endian
    uint32_t magic;         // 0x4D434150 "MCAP"
    uint8_t  version;       // 2
    uint8_t  bitsPerSample; // 16
    uint8_t  channels;      // 2
    uint8_t  isFloat;       // 0
    uint32_t sampleCount;   // Number of stereo frames
    uint32_t sampleRate;    // 48000
    uint64_t timestamp;     // Milliseconds
};
// Followed by: int16_t samples[sampleCount * channels]
```

## Command Line Interface

```bash
# List sources (JSON output)
SnackaCaptureWindows list --json

# Screen capture with H.264 encoding
SnackaCaptureWindows --display 0 --width 1920 --height 1080 --fps 30 --encode --bitrate 6

# Camera capture with H.264 encoding
SnackaCaptureWindows --camera 0 --width 640 --height 480 --fps 15 --encode --bitrate 2

# Screen capture with audio
SnackaCaptureWindows --display 0 --encode --audio
```

### Required Arguments

| Argument | Description |
|----------|-------------|
| `--display <index>` | Display index to capture |
| `--window <hwnd>` | Window handle to capture |
| `--camera <id>` | Camera device ID or index |
| `--width <pixels>` | Output width |
| `--height <pixels>` | Output height |
| `--fps <rate>` | Frames per second |
| `--encode` | **Required for Snacka** - Output H.264 instead of raw NV12 |
| `--bitrate <mbps>` | Encoding bitrate in Mbps |
| `--audio` | Capture system audio (screen share only) |

## Verification

### Test: Verify AVCC Output Format

```python
# Read first 4 bytes from stdout
length = int.from_bytes(data[0:4], 'big')  # Must be big-endian
nal_unit = data[4:4+length]
nal_type = nal_unit[0] & 0x1F

# First NAL on keyframe should be SPS (type 7) or PPS (type 8)
assert nal_type in [7, 8, 1, 5], f"Unexpected NAL type: {nal_type}"
```

### Test: Verify SPS/PPS Presence

On first keyframe, verify that SPS (type 7) and PPS (type 8) appear before IDR (type 5).

## Reference Implementation

The macOS implementation in `SnackaCaptureVideoToolbox` is the reference:

**VideoToolboxEncoder.swift:handleEncodedFrame()**
```swift
// The data is already in AVCC format (4-byte length prefix)
// Just copy it directly
nalData.append(Data(bytes: pointer, count: totalLength))
```

**Key insight:** VideoToolbox outputs AVCC format natively. Media Foundation on Windows outputs Annex-B and **must be converted to AVCC**.

## Windows-Specific Implementation Notes

Media Foundation encoders output H.264 in Annex-B format. The Windows implementation must:

1. Parse Annex-B start codes (00 00 00 01 or 00 00 01)
2. Convert each NAL unit to AVCC format (replace start code with 4-byte big-endian length)
3. Write to stdout

Example conversion (from MediaFoundationEncoder.cpp):
```cpp
void OutputNalUnits(const uint8_t* data, size_t size, bool isKeyframe) {
    // MFT outputs H.264 in Annex-B format
    // Convert to AVCC format (4-byte big-endian length prefix)

    while (pos < size) {
        // Find start code...
        // Calculate NAL length...

        uint32_t lenBE = htonl(nalLen);  // Convert to big-endian
        output.write(&lenBE, 4);
        output.write(nalData, nalLen);
    }
}
```

## Common Mistakes to Avoid

1. **Outputting Annex-B format** - The C# client will fail to parse
2. **Missing SPS/PPS on keyframes** - Decoder initialization will fail
3. **Little-endian length prefix** - Must be big-endian (network byte order)
4. **Including start codes in output** - Strip them, use length prefix only
5. **Not encoding to H.264** - Raw NV12 cannot be transmitted over WebRTC

## C# Client Expectations

See `CameraManager.cs:ProcessNalUnit()` for how the client parses AVCC data:

```csharp
// Read 4-byte NAL length (big-endian)
var nalLength = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));
offset += 4;

// Read NAL data
var nalData = buffer.AsSpan(offset, (int)nalLength);
var nalType = nalData[0] & 0x1F;
```
