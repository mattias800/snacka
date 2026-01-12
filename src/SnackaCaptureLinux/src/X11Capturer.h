#pragma once

#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <X11/extensions/XShm.h>
#include <sys/shm.h>

#include <functional>
#include <thread>
#include <atomic>
#include <vector>
#include <cstdint>

namespace snacka {

/// Callback for captured frames
/// @param data Pointer to NV12 frame data
/// @param size Size of the data
/// @param timestamp Timestamp in milliseconds
using FrameCallback = std::function<void(const uint8_t* data, size_t size, uint64_t timestamp)>;

/// X11 screen capturer using XShm for efficient capture
class X11Capturer {
public:
    X11Capturer();
    ~X11Capturer();

    /// Initialize the capturer
    /// @param displayIndex Index of the display to capture (currently 0 = root window)
    /// @param width Output width (capture will be scaled if different from screen)
    /// @param height Output height
    /// @param fps Target frames per second
    /// @return true if initialization succeeded
    bool Initialize(int displayIndex, int width, int height, int fps);

    /// Start capturing
    /// @param callback Callback to receive captured frames
    void Start(FrameCallback callback);

    /// Stop capturing
    void Stop();

    /// Check if capturing is running
    bool IsRunning() const { return m_running; }

    /// Get the screen width
    int GetScreenWidth() const { return m_screenWidth; }

    /// Get the screen height
    int GetScreenHeight() const { return m_screenHeight; }

private:
    void CaptureLoop();
    void ConvertBGRAtoNV12(const uint8_t* bgra, int srcWidth, int srcHeight);
    uint64_t GetTimestampMs() const;

    // X11 objects
    Display* m_display = nullptr;
    Window m_rootWindow = 0;
    XShmSegmentInfo m_shmInfo = {};
    XImage* m_image = nullptr;
    bool m_shmAttached = false;

    // Configuration
    int m_displayIndex = 0;
    int m_width = 0;
    int m_height = 0;
    int m_fps = 30;

    // Screen dimensions
    int m_screenWidth = 0;
    int m_screenHeight = 0;

    // Thread control
    std::atomic<bool> m_running{false};
    std::thread m_captureThread;

    // Callback
    FrameCallback m_callback;

    // NV12 output buffer
    std::vector<uint8_t> m_nv12Buffer;
};

}  // namespace snacka
