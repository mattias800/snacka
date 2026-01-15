#pragma once

#include "Protocol.h"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>

#include <atomic>
#include <functional>
#include <string>
#include <thread>

namespace snacka {

// Callback for frame data (same as DisplayCapturer/WindowCapturer)
using CameraFrameCallback = std::function<void(const uint8_t* nv12Data, size_t size, uint64_t timestamp)>;

/// Camera capture using Media Foundation IMFSourceReader.
/// Outputs NV12 frames compatible with MediaFoundationEncoder.
class CameraCapturer {
public:
    CameraCapturer();
    ~CameraCapturer();

    /// Initialize for a specific camera
    /// @param cameraId Device symbolic link or index as string
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
    bool CreateSourceReader(const std::string& cameraId);
    bool ConfigureMediaType();
    bool ConvertToNV12(IMFSample* sample, std::vector<uint8_t>& outNV12);

    // Configuration
    std::string m_cameraId;
    int m_requestedWidth = 640;
    int m_requestedHeight = 480;
    int m_requestedFps = 30;

    // Actual dimensions (may differ from requested)
    int m_width = 0;
    int m_height = 0;

    // State
    std::atomic<bool> m_running{false};
    std::thread m_captureThread;

    // Media Foundation objects
    IMFSourceReader* m_sourceReader = nullptr;
    IMFMediaType* m_outputType = nullptr;

    // Format info
    bool m_isNV12Native = false;  // True if camera outputs NV12 directly
    GUID m_nativeFormat = GUID_NULL;

    // Callback
    CameraFrameCallback m_callback;

    // Timing
    LARGE_INTEGER m_frequency;
    LARGE_INTEGER m_startTime;
};

}  // namespace snacka
