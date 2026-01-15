#pragma once

#include "Protocol.h"

#include <linux/videodev2.h>

#include <atomic>
#include <functional>
#include <string>
#include <thread>
#include <vector>

namespace snacka {

// Callback for frame data (same signature as X11Capturer)
using CameraFrameCallback = std::function<void(const uint8_t* nv12Data, size_t size, uint64_t timestamp)>;

/// Camera capture using Video4Linux2.
/// Outputs NV12 frames compatible with VaapiEncoder.
/// Handles format negotiation and YUYV to NV12 conversion for webcams.
class V4L2Capturer {
public:
    V4L2Capturer();
    ~V4L2Capturer();

    /// Initialize for a specific camera
    /// @param cameraId Device path (e.g., /dev/video0) or index as string
    /// @param width Requested output width
    /// @param height Requested output height
    /// @param fps Requested frame rate
    /// @return true if initialization succeeded
    bool Initialize(const std::string& cameraId, int width, int height, int fps);

    /// Start capturing - calls callback for each frame
    void Start(CameraFrameCallback callback);

    /// Stop capturing
    void Stop();

    /// Check if currently capturing
    bool IsRunning() const { return m_running; }

    /// Get actual capture dimensions
    int GetWidth() const { return m_width; }
    int GetHeight() const { return m_height; }

private:
    void CaptureLoop();
    bool OpenDevice(const std::string& cameraId);
    bool InitMmap();
    bool StartStreaming();
    void StopStreaming();
    void CleanupMmap();
    bool NegotiateFormat();
    void ConvertYUYVToNV12(const uint8_t* yuyv, uint8_t* nv12);

    // Configuration
    std::string m_devicePath;
    int m_requestedWidth = 640;
    int m_requestedHeight = 480;
    int m_requestedFps = 30;

    // Actual dimensions (may differ from requested)
    int m_width = 0;
    int m_height = 0;

    // State
    std::atomic<bool> m_running{false};
    std::thread m_captureThread;
    int m_fd = -1;

    // Format info
    uint32_t m_pixelFormat = 0;
    bool m_needsConversion = false;  // True if camera doesn't output NV12 natively

    // Memory-mapped buffers
    struct MmapBuffer {
        void* start = nullptr;
        size_t length = 0;
    };
    std::vector<MmapBuffer> m_buffers;
    static constexpr int NUM_BUFFERS = 4;

    // NV12 conversion buffer
    std::vector<uint8_t> m_nv12Buffer;

    // Callback
    CameraFrameCallback m_callback;

    // Timing
    struct timespec m_startTime;
};

}  // namespace snacka
