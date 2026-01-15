#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace snacka {

// Audio packet header format - must match SCREEN_CAPTURE_PROTOCOL.md
// Total size: 24 bytes
#pragma pack(push, 1)
struct AudioPacketHeader {
    uint32_t magic;          // 0x4D434150 "MCAP" little-endian
    uint8_t  version;        // 2
    uint8_t  bitsPerSample;  // 16
    uint8_t  channels;       // 2
    uint8_t  isFloat;        // 0
    uint32_t sampleCount;    // Number of stereo frames
    uint32_t sampleRate;     // 48000
    uint64_t timestamp;      // Milliseconds

    static constexpr uint32_t MAGIC = 0x4D434150;  // "MCAP" in little-endian
    static constexpr uint8_t VERSION = 2;

    AudioPacketHeader() = default;
    AudioPacketHeader(uint32_t samples, uint64_t ts)
        : magic(MAGIC)
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

// Capture source types
enum class SourceType {
    Display,
    Window
};

// Capture configuration
struct CaptureConfig {
    SourceType sourceType = SourceType::Display;
    int sourceIndex = 0;           // Display index or window handle
    std::wstring windowTitle;      // For window capture by title
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
    std::string id;          // HWND as string
    std::string name;        // Window title
    std::string appName;     // Process name
    std::string bundleId;    // Empty on Windows (macOS concept)
};

struct CameraInfo {
    std::string id;          // Device symbolic link (unique identifier)
    std::string name;        // Friendly device name
    int index;               // Index in device list
};

struct SourceList {
    std::vector<DisplayInfo> displays;
    std::vector<WindowInfo> windows;
    std::vector<std::string> applications;  // Empty on Windows
    std::vector<CameraInfo> cameras;
};

// Calculate NV12 frame size
inline size_t CalculateNV12FrameSize(int width, int height) {
    // Y plane: width * height
    // UV plane: width * height / 2 (interleaved U,V at half height)
    return static_cast<size_t>(width) * height * 3 / 2;
}

}  // namespace snacka
