#pragma once

#include <cstdint>
#include <string>
#include <vector>
#include <arpa/inet.h>

namespace snacka {

// Helper to convert 64-bit to big-endian
inline uint64_t ToBigEndian64(uint64_t host) {
#if __BYTE_ORDER__ == __ORDER_LITTLE_ENDIAN__
    return ((host & 0x00000000000000FFULL) << 56) |
           ((host & 0x000000000000FF00ULL) << 40) |
           ((host & 0x0000000000FF0000ULL) << 24) |
           ((host & 0x00000000FF000000ULL) << 8) |
           ((host & 0x000000FF00000000ULL) >> 8) |
           ((host & 0x0000FF0000000000ULL) >> 24) |
           ((host & 0x00FF000000000000ULL) >> 40) |
           ((host & 0xFF00000000000000ULL) >> 56);
#else
    return host;
#endif
}

// Audio packet header format - must match SCREEN_CAPTURE_PROTOCOL.md
// Total size: 24 bytes
// All multi-byte fields use big-endian (network byte order) for consistency
#pragma pack(push, 1)
struct AudioPacketHeader {
    uint32_t magic;          // 0x4D434150 "MCAP" big-endian
    uint8_t  version;        // 2
    uint8_t  bitsPerSample;  // 16
    uint8_t  channels;       // 2
    uint8_t  isFloat;        // 0
    uint32_t sampleCount;    // Number of stereo frames
    uint32_t sampleRate;     // 48000
    uint64_t timestamp;      // Milliseconds

    static constexpr uint32_t MAGIC = 0x4D434150;  // "MCAP" in big-endian
    static constexpr uint8_t VERSION = 2;

    AudioPacketHeader() = default;
    AudioPacketHeader(uint32_t samples, uint64_t ts)
        : magic(htonl(MAGIC))
        , version(VERSION)
        , bitsPerSample(16)
        , channels(2)
        , isFloat(0)
        , sampleCount(samples)
        , sampleRate(48000)
        , timestamp(ts) {}
};
#pragma pack(pop)

static_assert(sizeof(AudioPacketHeader) == 24, "AudioPacketHeader must be 24 bytes");

// Preview frame packet header for stderr unified protocol
// Format: [magic: 4] [length: 4] [width: 2] [height: 2] [format: 1] [timestamp: 8] [pixels...]
// All multi-byte fields are big-endian
enum class PreviewFormat : uint8_t {
    NV12 = 0,   // NV12 (width * height * 1.5 bytes)
    RGB24 = 1,  // RGB24 (width * height * 3 bytes)
    RGBA32 = 2  // RGBA32 (width * height * 4 bytes)
};

#pragma pack(push, 1)
struct PreviewPacketHeader {
    uint32_t magic;      // 0x50524556 "PREV" big-endian
    uint32_t length;     // Payload length (big-endian)
    uint16_t width;      // Frame width (big-endian)
    uint16_t height;     // Frame height (big-endian)
    uint8_t  format;     // PreviewFormat value
    uint64_t timestamp;  // Milliseconds (big-endian)

    static constexpr uint32_t MAGIC = 0x50524556;  // "PREV" in big-endian

    PreviewPacketHeader() = default;
    PreviewPacketHeader(uint16_t w, uint16_t h, PreviewFormat fmt, uint64_t ts, uint32_t pixelDataSize)
        : magic(htonl(MAGIC))
        , length(htonl(2 + 2 + 1 + 8 + pixelDataSize))
        , width(htons(w))
        , height(htons(h))
        , format(static_cast<uint8_t>(fmt))
        , timestamp(ToBigEndian64(ts)) {}
};
#pragma pack(pop)

static_assert(sizeof(PreviewPacketHeader) == 21, "PreviewPacketHeader must be 21 bytes");

// Log level values
enum class LogLevel : uint8_t {
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3
};

// Capture source types
enum class SourceType {
    Display,
    Window
};

// Capture configuration
struct CaptureConfig {
    SourceType sourceType = SourceType::Display;
    int sourceIndex = 0;           // Display index or X11 window ID
    std::string windowTitle;       // For window capture by title
    int width = 1920;
    int height = 1080;
    int fps = 30;
    bool captureAudio = false;
};

// Source information for listing
struct DisplayInfo {
    std::string id;
    std::string name;
    int width;
    int height;
    bool isPrimary;
};

struct WindowInfo {
    std::string id;          // X11 Window ID as string
    std::string name;        // Window title
    std::string appName;     // Process name
    std::string bundleId;    // Empty on Linux (macOS concept)
};

struct CameraInfo {
    std::string id;          // Device path (e.g., /dev/video0)
    std::string name;        // Device name from V4L2
    int index;               // Index in device list
};

struct MicrophoneInfo {
    std::string id;          // PulseAudio source name
    std::string name;        // Human-readable device name
    int index;               // Index in device list
};

struct SourceList {
    std::vector<DisplayInfo> displays;
    std::vector<WindowInfo> windows;
    std::vector<std::string> applications;
    std::vector<CameraInfo> cameras;
    std::vector<MicrophoneInfo> microphones;
};

// Calculate NV12 frame size
inline size_t CalculateNV12FrameSize(int width, int height) {
    // Y plane: width * height
    // UV plane: width * height / 2 (interleaved U,V at half height)
    return static_cast<size_t>(width) * height * 3 / 2;
}

}  // namespace snacka
