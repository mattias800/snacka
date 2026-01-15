#include "V4L2Capturer.h"

#include <sys/ioctl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <fcntl.h>
#include <unistd.h>
#include <poll.h>

#include <iostream>
#include <cstring>
#include <algorithm>

namespace snacka {

V4L2Capturer::V4L2Capturer() {
    clock_gettime(CLOCK_MONOTONIC, &m_startTime);
}

V4L2Capturer::~V4L2Capturer() {
    Stop();
    CleanupMmap();
    if (m_fd >= 0) {
        close(m_fd);
        m_fd = -1;
    }
}

bool V4L2Capturer::Initialize(const std::string& cameraId, int width, int height, int fps) {
    m_requestedWidth = width;
    m_requestedHeight = height;
    m_requestedFps = fps;

    // Open the device
    if (!OpenDevice(cameraId)) {
        return false;
    }

    // Negotiate format
    if (!NegotiateFormat()) {
        close(m_fd);
        m_fd = -1;
        return false;
    }

    // Initialize memory-mapped buffers
    if (!InitMmap()) {
        close(m_fd);
        m_fd = -1;
        return false;
    }

    // Allocate conversion buffer if needed
    auto nv12Size = CalculateNV12FrameSize(m_width, m_height);
    m_nv12Buffer.resize(nv12Size);

    std::cerr << "V4L2Capturer: Initialized " << m_width << "x" << m_height
              << " @ " << m_requestedFps << "fps"
              << " (format: " << (m_needsConversion ? "YUYV->NV12" : "NV12") << ")\n";

    return true;
}

bool V4L2Capturer::OpenDevice(const std::string& cameraId) {
    // Check if cameraId is an index or a device path
    if (cameraId.find("/dev/") == 0) {
        m_devicePath = cameraId;
    } else {
        // Try to parse as index
        try {
            int index = std::stoi(cameraId);
            m_devicePath = "/dev/video" + std::to_string(index);
        } catch (...) {
            // Assume it's a device path
            m_devicePath = cameraId;
        }
    }

    m_fd = open(m_devicePath.c_str(), O_RDWR | O_NONBLOCK);
    if (m_fd < 0) {
        std::cerr << "V4L2Capturer: Failed to open device " << m_devicePath << ": " << strerror(errno) << "\n";
        return false;
    }

    // Verify it's a video capture device
    struct v4l2_capability cap;
    if (ioctl(m_fd, VIDIOC_QUERYCAP, &cap) < 0) {
        std::cerr << "V4L2Capturer: VIDIOC_QUERYCAP failed: " << strerror(errno) << "\n";
        close(m_fd);
        m_fd = -1;
        return false;
    }

    if (!(cap.device_caps & V4L2_CAP_VIDEO_CAPTURE)) {
        std::cerr << "V4L2Capturer: Device is not a video capture device\n";
        close(m_fd);
        m_fd = -1;
        return false;
    }

    if (!(cap.device_caps & V4L2_CAP_STREAMING)) {
        std::cerr << "V4L2Capturer: Device does not support streaming\n";
        close(m_fd);
        m_fd = -1;
        return false;
    }

    std::cerr << "V4L2Capturer: Opened " << cap.card << " at " << m_devicePath << "\n";
    return true;
}

bool V4L2Capturer::NegotiateFormat() {
    struct v4l2_format fmt;
    memset(&fmt, 0, sizeof(fmt));
    fmt.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;

    // Try NV12 first (ideal for encoding)
    fmt.fmt.pix.width = m_requestedWidth;
    fmt.fmt.pix.height = m_requestedHeight;
    fmt.fmt.pix.pixelformat = V4L2_PIX_FMT_NV12;
    fmt.fmt.pix.field = V4L2_FIELD_NONE;

    if (ioctl(m_fd, VIDIOC_S_FMT, &fmt) == 0 && fmt.fmt.pix.pixelformat == V4L2_PIX_FMT_NV12) {
        m_pixelFormat = V4L2_PIX_FMT_NV12;
        m_needsConversion = false;
        m_width = fmt.fmt.pix.width;
        m_height = fmt.fmt.pix.height;
        std::cerr << "V4L2Capturer: Using NV12 format\n";
        goto set_fps;
    }

    // Try YUYV (common webcam format)
    fmt.fmt.pix.width = m_requestedWidth;
    fmt.fmt.pix.height = m_requestedHeight;
    fmt.fmt.pix.pixelformat = V4L2_PIX_FMT_YUYV;
    fmt.fmt.pix.field = V4L2_FIELD_NONE;

    if (ioctl(m_fd, VIDIOC_S_FMT, &fmt) == 0 && fmt.fmt.pix.pixelformat == V4L2_PIX_FMT_YUYV) {
        m_pixelFormat = V4L2_PIX_FMT_YUYV;
        m_needsConversion = true;
        m_width = fmt.fmt.pix.width;
        m_height = fmt.fmt.pix.height;
        std::cerr << "V4L2Capturer: Using YUYV format (will convert to NV12)\n";
        goto set_fps;
    }

    // Try MJPEG as last resort (would need additional decoding, not implemented)
    std::cerr << "V4L2Capturer: No supported pixel format found\n";
    return false;

set_fps:
    // Set frame rate
    struct v4l2_streamparm parm;
    memset(&parm, 0, sizeof(parm));
    parm.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    parm.parm.capture.timeperframe.numerator = 1;
    parm.parm.capture.timeperframe.denominator = m_requestedFps;

    if (ioctl(m_fd, VIDIOC_S_PARM, &parm) < 0) {
        std::cerr << "V4L2Capturer: Warning - Could not set frame rate\n";
    }

    return true;
}

bool V4L2Capturer::InitMmap() {
    // Request buffers
    struct v4l2_requestbuffers req;
    memset(&req, 0, sizeof(req));
    req.count = NUM_BUFFERS;
    req.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    req.memory = V4L2_MEMORY_MMAP;

    if (ioctl(m_fd, VIDIOC_REQBUFS, &req) < 0) {
        std::cerr << "V4L2Capturer: VIDIOC_REQBUFS failed: " << strerror(errno) << "\n";
        return false;
    }

    if (req.count < 2) {
        std::cerr << "V4L2Capturer: Insufficient buffer memory\n";
        return false;
    }

    m_buffers.resize(req.count);

    // Map buffers
    for (unsigned int i = 0; i < req.count; i++) {
        struct v4l2_buffer buf;
        memset(&buf, 0, sizeof(buf));
        buf.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
        buf.memory = V4L2_MEMORY_MMAP;
        buf.index = i;

        if (ioctl(m_fd, VIDIOC_QUERYBUF, &buf) < 0) {
            std::cerr << "V4L2Capturer: VIDIOC_QUERYBUF failed: " << strerror(errno) << "\n";
            return false;
        }

        m_buffers[i].length = buf.length;
        m_buffers[i].start = mmap(nullptr, buf.length, PROT_READ | PROT_WRITE,
                                   MAP_SHARED, m_fd, buf.m.offset);

        if (m_buffers[i].start == MAP_FAILED) {
            std::cerr << "V4L2Capturer: mmap failed: " << strerror(errno) << "\n";
            return false;
        }
    }

    return true;
}

void V4L2Capturer::CleanupMmap() {
    for (auto& buf : m_buffers) {
        if (buf.start && buf.start != MAP_FAILED) {
            munmap(buf.start, buf.length);
        }
    }
    m_buffers.clear();
}

bool V4L2Capturer::StartStreaming() {
    // Queue all buffers
    for (unsigned int i = 0; i < m_buffers.size(); i++) {
        struct v4l2_buffer buf;
        memset(&buf, 0, sizeof(buf));
        buf.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
        buf.memory = V4L2_MEMORY_MMAP;
        buf.index = i;

        if (ioctl(m_fd, VIDIOC_QBUF, &buf) < 0) {
            std::cerr << "V4L2Capturer: VIDIOC_QBUF failed: " << strerror(errno) << "\n";
            return false;
        }
    }

    // Start streaming
    enum v4l2_buf_type type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    if (ioctl(m_fd, VIDIOC_STREAMON, &type) < 0) {
        std::cerr << "V4L2Capturer: VIDIOC_STREAMON failed: " << strerror(errno) << "\n";
        return false;
    }

    return true;
}

void V4L2Capturer::StopStreaming() {
    enum v4l2_buf_type type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    ioctl(m_fd, VIDIOC_STREAMOFF, &type);
}

void V4L2Capturer::Start(CameraFrameCallback callback) {
    if (m_running) return;

    m_callback = callback;

    if (!StartStreaming()) {
        std::cerr << "V4L2Capturer: Failed to start streaming\n";
        return;
    }

    m_running = true;
    clock_gettime(CLOCK_MONOTONIC, &m_startTime);
    m_captureThread = std::thread(&V4L2Capturer::CaptureLoop, this);
}

void V4L2Capturer::Stop() {
    if (!m_running) return;

    m_running = false;

    if (m_captureThread.joinable()) {
        m_captureThread.join();
    }

    StopStreaming();
}

void V4L2Capturer::CaptureLoop() {
    uint64_t frameCount = 0;
    auto nv12Size = CalculateNV12FrameSize(m_width, m_height);

    std::cerr << "V4L2Capturer: Capture loop starting\n";

    while (m_running) {
        // Poll for frame
        struct pollfd pfd;
        pfd.fd = m_fd;
        pfd.events = POLLIN;

        int ret = poll(&pfd, 1, 100);  // 100ms timeout
        if (ret < 0) {
            if (errno == EINTR) continue;
            std::cerr << "V4L2Capturer: poll failed: " << strerror(errno) << "\n";
            break;
        }

        if (ret == 0) {
            // Timeout, no frame available
            continue;
        }

        // Dequeue buffer
        struct v4l2_buffer buf;
        memset(&buf, 0, sizeof(buf));
        buf.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
        buf.memory = V4L2_MEMORY_MMAP;

        if (ioctl(m_fd, VIDIOC_DQBUF, &buf) < 0) {
            if (errno == EAGAIN) continue;
            std::cerr << "V4L2Capturer: VIDIOC_DQBUF failed: " << strerror(errno) << "\n";
            break;
        }

        // Calculate timestamp
        struct timespec now;
        clock_gettime(CLOCK_MONOTONIC, &now);
        uint64_t elapsedMs = static_cast<uint64_t>(
            (now.tv_sec - m_startTime.tv_sec) * 1000 +
            (now.tv_nsec - m_startTime.tv_nsec) / 1000000
        );

        // Get frame data
        const uint8_t* frameData = static_cast<const uint8_t*>(m_buffers[buf.index].start);

        if (m_needsConversion) {
            // Convert YUYV to NV12
            ConvertYUYVToNV12(frameData, m_nv12Buffer.data());
            frameData = m_nv12Buffer.data();
        }

        frameCount++;
        if (frameCount <= 5 || frameCount % 100 == 0) {
            std::cerr << "V4L2Capturer: Frame " << frameCount
                      << " (" << m_width << "x" << m_height << " NV12)\n";
        }

        // Call callback
        if (m_callback) {
            m_callback(frameData, nv12Size, elapsedMs);
        }

        // Re-queue buffer
        if (ioctl(m_fd, VIDIOC_QBUF, &buf) < 0) {
            std::cerr << "V4L2Capturer: VIDIOC_QBUF failed: " << strerror(errno) << "\n";
            break;
        }
    }

    std::cerr << "V4L2Capturer: Capture loop ended (" << frameCount << " frames)\n";
}

void V4L2Capturer::ConvertYUYVToNV12(const uint8_t* yuyv, uint8_t* nv12) {
    // YUYV format: Y0 U0 Y1 V0 Y2 U1 Y3 V1 ...
    // NV12 format: Y plane (full resolution), then interleaved UV plane (half height)

    int yPlaneSize = m_width * m_height;
    uint8_t* yPlane = nv12;
    uint8_t* uvPlane = nv12 + yPlaneSize;

    // Extract Y values (every other byte from YUYV)
    for (int y = 0; y < m_height; y++) {
        const uint8_t* yuyvRow = yuyv + y * m_width * 2;
        uint8_t* yRow = yPlane + y * m_width;

        for (int x = 0; x < m_width; x++) {
            yRow[x] = yuyvRow[x * 2];
        }
    }

    // Extract UV values (subsample from every other row)
    for (int y = 0; y < m_height / 2; y++) {
        // Average UV from two rows
        const uint8_t* yuyvRow0 = yuyv + (y * 2) * m_width * 2;
        const uint8_t* yuyvRow1 = yuyv + (y * 2 + 1) * m_width * 2;
        uint8_t* uvRow = uvPlane + y * m_width;

        for (int x = 0; x < m_width / 2; x++) {
            // U comes first in YUYV
            int u0 = yuyvRow0[x * 4 + 1];
            int u1 = yuyvRow1[x * 4 + 1];
            // V comes second
            int v0 = yuyvRow0[x * 4 + 3];
            int v1 = yuyvRow1[x * 4 + 3];

            // Average vertically
            uvRow[x * 2] = static_cast<uint8_t>((u0 + u1) / 2);
            uvRow[x * 2 + 1] = static_cast<uint8_t>((v0 + v1) / 2);
        }
    }
}

}  // namespace snacka
