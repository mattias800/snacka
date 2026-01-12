#include "X11Capturer.h"
#include "Protocol.h"

#include <iostream>
#include <chrono>
#include <cstring>
#include <algorithm>

namespace snacka {

X11Capturer::X11Capturer() {
}

X11Capturer::~X11Capturer() {
    Stop();

    if (m_image) {
        XDestroyImage(m_image);
        m_image = nullptr;
    }

    if (m_shmAttached) {
        XShmDetach(m_display, &m_shmInfo);
        shmdt(m_shmInfo.shmaddr);
        shmctl(m_shmInfo.shmid, IPC_RMID, nullptr);
        m_shmAttached = false;
    }

    if (m_display) {
        XCloseDisplay(m_display);
        m_display = nullptr;
    }
}

bool X11Capturer::Initialize(int displayIndex, int width, int height, int fps) {
    m_displayIndex = displayIndex;
    m_width = width;
    m_height = height;
    m_fps = fps;

    // Open X display
    m_display = XOpenDisplay(nullptr);
    if (!m_display) {
        std::cerr << "SnackaCaptureLinux: Failed to open X display\n";
        return false;
    }

    // Get root window of default screen
    int screen = DefaultScreen(m_display);
    m_rootWindow = RootWindow(m_display, screen);

    // Get screen dimensions
    m_screenWidth = DisplayWidth(m_display, screen);
    m_screenHeight = DisplayHeight(m_display, screen);

    std::cerr << "SnackaCaptureLinux: Screen dimensions: " << m_screenWidth << "x" << m_screenHeight << "\n";

    // Check for XShm extension
    if (!XShmQueryExtension(m_display)) {
        std::cerr << "SnackaCaptureLinux: XShm extension not available\n";
        return false;
    }

    // Create shared memory XImage
    Visual* visual = DefaultVisual(m_display, screen);
    int depth = DefaultDepth(m_display, screen);

    m_image = XShmCreateImage(
        m_display,
        visual,
        depth,
        ZPixmap,
        nullptr,
        &m_shmInfo,
        m_screenWidth,
        m_screenHeight
    );

    if (!m_image) {
        std::cerr << "SnackaCaptureLinux: Failed to create XShm image\n";
        return false;
    }

    // Allocate shared memory
    m_shmInfo.shmid = shmget(IPC_PRIVATE, m_image->bytes_per_line * m_image->height, IPC_CREAT | 0777);
    if (m_shmInfo.shmid < 0) {
        std::cerr << "SnackaCaptureLinux: Failed to allocate shared memory\n";
        XDestroyImage(m_image);
        m_image = nullptr;
        return false;
    }

    m_shmInfo.shmaddr = m_image->data = static_cast<char*>(shmat(m_shmInfo.shmid, nullptr, 0));
    if (m_shmInfo.shmaddr == reinterpret_cast<char*>(-1)) {
        std::cerr << "SnackaCaptureLinux: Failed to attach shared memory\n";
        shmctl(m_shmInfo.shmid, IPC_RMID, nullptr);
        XDestroyImage(m_image);
        m_image = nullptr;
        return false;
    }

    m_shmInfo.readOnly = False;

    if (!XShmAttach(m_display, &m_shmInfo)) {
        std::cerr << "SnackaCaptureLinux: Failed to attach XShm\n";
        shmdt(m_shmInfo.shmaddr);
        shmctl(m_shmInfo.shmid, IPC_RMID, nullptr);
        XDestroyImage(m_image);
        m_image = nullptr;
        return false;
    }

    m_shmAttached = true;

    // Allocate NV12 buffer for output
    m_nv12Buffer.resize(CalculateNV12FrameSize(m_width, m_height));

    std::cerr << "SnackaCaptureLinux: X11 capture initialized for output "
              << m_width << "x" << m_height << " @ " << m_fps << "fps\n";

    return true;
}

void X11Capturer::Start(FrameCallback callback) {
    if (m_running) {
        return;
    }

    m_callback = callback;
    m_running = true;
    m_captureThread = std::thread(&X11Capturer::CaptureLoop, this);
}

void X11Capturer::Stop() {
    m_running = false;
    if (m_captureThread.joinable()) {
        m_captureThread.join();
    }
}

void X11Capturer::CaptureLoop() {
    auto frameInterval = std::chrono::microseconds(1000000 / m_fps);
    auto nextFrameTime = std::chrono::steady_clock::now();

    while (m_running) {
        auto startTime = std::chrono::steady_clock::now();

        // Capture screen using XShm
        if (!XShmGetImage(m_display, m_rootWindow, m_image, 0, 0, AllPlanes)) {
            std::cerr << "SnackaCaptureLinux: XShmGetImage failed\n";
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
            continue;
        }

        // Convert BGRA to NV12
        ConvertBGRAtoNV12(
            reinterpret_cast<const uint8_t*>(m_image->data),
            m_screenWidth,
            m_screenHeight
        );

        // Invoke callback with NV12 data
        if (m_callback) {
            uint64_t timestamp = GetTimestampMs();
            m_callback(m_nv12Buffer.data(), m_nv12Buffer.size(), timestamp);
        }

        // Frame rate control
        nextFrameTime += frameInterval;
        auto now = std::chrono::steady_clock::now();
        if (nextFrameTime > now) {
            std::this_thread::sleep_until(nextFrameTime);
        } else {
            // We're behind, reset the timing
            nextFrameTime = now;
        }
    }
}

void X11Capturer::ConvertBGRAtoNV12(const uint8_t* bgra, int srcWidth, int srcHeight) {
    // Simple conversion with scaling if needed
    // For now, we'll do nearest-neighbor scaling if dimensions differ

    float scaleX = static_cast<float>(srcWidth) / m_width;
    float scaleY = static_cast<float>(srcHeight) / m_height;

    uint8_t* yPlane = m_nv12Buffer.data();
    uint8_t* uvPlane = m_nv12Buffer.data() + m_width * m_height;

    int srcBytesPerPixel = m_image->bits_per_pixel / 8;
    int srcStride = m_image->bytes_per_line;

    // Convert to Y plane (full resolution)
    for (int y = 0; y < m_height; y++) {
        int srcY = static_cast<int>(y * scaleY);
        srcY = std::min(srcY, srcHeight - 1);

        for (int x = 0; x < m_width; x++) {
            int srcX = static_cast<int>(x * scaleX);
            srcX = std::min(srcX, srcWidth - 1);

            const uint8_t* pixel = bgra + srcY * srcStride + srcX * srcBytesPerPixel;
            uint8_t b = pixel[0];
            uint8_t g = pixel[1];
            uint8_t r = pixel[2];

            // BT.601 conversion
            int yVal = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
            yPlane[y * m_width + x] = static_cast<uint8_t>(std::clamp(yVal, 0, 255));
        }
    }

    // Convert to UV plane (half resolution, interleaved)
    for (int y = 0; y < m_height / 2; y++) {
        int srcY = static_cast<int>(y * 2 * scaleY);
        srcY = std::min(srcY, srcHeight - 1);

        for (int x = 0; x < m_width / 2; x++) {
            int srcX = static_cast<int>(x * 2 * scaleX);
            srcX = std::min(srcX, srcWidth - 1);

            // Sample 2x2 block and average
            int rSum = 0, gSum = 0, bSum = 0;
            int count = 0;

            for (int dy = 0; dy < 2; dy++) {
                for (int dx = 0; dx < 2; dx++) {
                    int px = std::min(static_cast<int>((x * 2 + dx) * scaleX), srcWidth - 1);
                    int py = std::min(static_cast<int>((y * 2 + dy) * scaleY), srcHeight - 1);

                    const uint8_t* pixel = bgra + py * srcStride + px * srcBytesPerPixel;
                    bSum += pixel[0];
                    gSum += pixel[1];
                    rSum += pixel[2];
                    count++;
                }
            }

            int r = rSum / count;
            int g = gSum / count;
            int b = bSum / count;

            // BT.601 conversion
            int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
            int v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;

            int uvIndex = y * m_width + x * 2;
            uvPlane[uvIndex] = static_cast<uint8_t>(std::clamp(u, 0, 255));
            uvPlane[uvIndex + 1] = static_cast<uint8_t>(std::clamp(v, 0, 255));
        }
    }
}

uint64_t X11Capturer::GetTimestampMs() const {
    auto now = std::chrono::steady_clock::now();
    auto duration = now.time_since_epoch();
    return std::chrono::duration_cast<std::chrono::milliseconds>(duration).count();
}

}  // namespace snacka
