#include "Protocol.h"
#include "SourceLister.h"
#include "X11Capturer.h"
#include "V4L2Capturer.h"
#include "VaapiEncoder.h"

#include <iostream>
#include <string>
#include <vector>
#include <atomic>
#include <csignal>
#include <unistd.h>

using namespace snacka;

// Global flag for clean shutdown
std::atomic<bool> g_running{true};

// Signal handler for clean shutdown
void SignalHandler(int signal) {
    if (signal == SIGINT || signal == SIGTERM || signal == SIGPIPE) {
        std::cerr << "\nSnackaCaptureLinux: Received shutdown signal\n";
        g_running = false;
    }
}

void PrintUsage() {
    std::cerr << R"(
SnackaCaptureLinux - Screen and camera capture tool for Linux with VAAPI encoding

USAGE:
    SnackaCaptureLinux list [--json]
    SnackaCaptureLinux [OPTIONS]

COMMANDS:
    list              List available capture sources (displays, windows, cameras)

OPTIONS:
    --display <index>   Display index to capture (default: 0)
    --camera <id>       Camera device path or index to capture (e.g., /dev/video0 or 0)
    --width <pixels>    Output width (default: 1920, camera: 640)
    --height <pixels>   Output height (default: 1080, camera: 480)
    --fps <rate>        Frames per second (default: 30, camera: 15)
    --encode            Output H.264 encoded video (instead of raw NV12)
    --bitrate <mbps>    Encoding bitrate in Mbps (default: 6, camera: 2)
    --json              Output source list as JSON (with 'list' command)
    --help              Show this help message

EXAMPLES:
    SnackaCaptureLinux list --json
    SnackaCaptureLinux --display 0 --width 1920 --height 1080 --fps 30
    SnackaCaptureLinux --display 0 --encode --bitrate 8
    SnackaCaptureLinux --camera 0 --encode --bitrate 2
    SnackaCaptureLinux --camera /dev/video0 --width 640 --height 480 --fps 15

OUTPUT:
    Without --encode: Raw NV12 video frames to stdout
    With --encode: H.264 NAL units in AVCC format (4-byte length prefix) to stdout
)";
}

int ListSources(bool asJson) {
    auto sources = SourceLister::GetAvailableSources();

    if (asJson) {
        SourceLister::PrintSourcesAsJson(sources);
    } else {
        SourceLister::PrintSources(sources);
    }

    return 0;
}

int Capture(int displayIndex, const std::string& cameraId, int width, int height, int fps, bool encodeH264, int bitrateMbps) {
    // Set up signal handlers for clean shutdown
    signal(SIGINT, SignalHandler);
    signal(SIGTERM, SignalHandler);
    signal(SIGPIPE, SignalHandler);

    std::string sourceType = !cameraId.empty() ? "camera" : "display";
    std::cerr << "SnackaCaptureLinux: Starting " << sourceType << " capture "
              << width << "x" << height << " @ " << fps << "fps"
              << (encodeH264 ? ", encode=H.264 @ " + std::to_string(bitrateMbps) + "Mbps" : ", encode=raw NV12")
              << "\n";

    // Frame statistics
    uint64_t frameCount = 0;
    uint64_t encodedFrameCount = 0;

    // Initialize H.264 encoder if requested
    std::unique_ptr<VaapiEncoder> encoder;
    if (encodeH264) {
        if (!VaapiEncoder::IsHardwareEncoderAvailable()) {
            std::cerr << "SnackaCaptureLinux: WARNING - No VAAPI H.264 encoder available, falling back to raw NV12\n";
            encodeH264 = false;
        } else {
            encoder = std::make_unique<VaapiEncoder>(width, height, fps, bitrateMbps);

            if (!encoder->Initialize()) {
                std::cerr << "SnackaCaptureLinux: WARNING - Failed to initialize VAAPI encoder, falling back to raw NV12\n";
                encoder.reset();
                encodeH264 = false;
            } else {
                std::cerr << "SnackaCaptureLinux: Using " << encoder->GetEncoderName() << " encoder\n";
            }
        }
    }

    if (encodeH264 && encoder) {
        // Set callback for encoded data
        encoder->SetCallback([&](const uint8_t* data, size_t size, bool isKeyframe) {
            if (!g_running) return;

            size_t written = 0;
            while (written < size && g_running) {
                ssize_t result = write(STDOUT_FILENO, data + written, size - written);
                if (result < 0) {
                    if (errno == EPIPE) {
                        std::cerr << "SnackaCaptureLinux: Pipe closed\n";
                    } else {
                        std::cerr << "SnackaCaptureLinux: Error writing encoded frame\n";
                    }
                    g_running = false;
                    return;
                }
                written += result;
            }

            encodedFrameCount++;
            if (encodedFrameCount <= 5 || encodedFrameCount % 100 == 0) {
                std::cerr << "SnackaCaptureLinux: Encoded frame " << encodedFrameCount
                          << " (" << size << " bytes" << (isKeyframe ? ", keyframe" : "") << ")\n";
            }
        });
    }

    // Frame callback
    auto frameCallback = [&](const uint8_t* data, size_t size, uint64_t timestamp) {
        if (!g_running) return;

        frameCount++;

        if (encodeH264 && encoder) {
            // Encode to H.264
            if (!encoder->EncodeNV12(data, size, static_cast<int64_t>(timestamp))) {
                if (frameCount <= 5) {
                    std::cerr << "SnackaCaptureLinux: Warning - Failed to encode frame " << frameCount << "\n";
                }
            }
        } else {
            // Output raw NV12
            size_t written = 0;
            while (written < size && g_running) {
                ssize_t result = write(STDOUT_FILENO, data + written, size - written);
                if (result < 0) {
                    if (errno == EPIPE) {
                        std::cerr << "SnackaCaptureLinux: Pipe closed\n";
                    } else {
                        std::cerr << "SnackaCaptureLinux: Error writing video frame\n";
                    }
                    g_running = false;
                    return;
                }
                written += result;
            }

            if (frameCount <= 5 || frameCount % 100 == 0) {
                std::cerr << "SnackaCaptureLinux: Video frame " << frameCount
                          << " (" << width << "x" << height << " NV12, " << size << " bytes)\n";
            }
        }
    };

    // Start video capture
    bool captureStarted = false;

    if (!cameraId.empty()) {
        // Camera capture using V4L2
        V4L2Capturer capturer;
        if (capturer.Initialize(cameraId, width, height, fps)) {
            capturer.Start(frameCallback);
            captureStarted = true;

            // Wait for shutdown
            while (g_running && capturer.IsRunning()) {
                usleep(100000);  // 100ms
            }

            capturer.Stop();
        } else {
            std::cerr << "SnackaCaptureLinux: Failed to initialize V4L2 camera capture\n";
        }
    } else {
        // Display capture using X11
        X11Capturer capturer;
        if (capturer.Initialize(displayIndex, width, height, fps)) {
            capturer.Start(frameCallback);
            captureStarted = true;

            // Wait for shutdown
            while (g_running && capturer.IsRunning()) {
                usleep(100000);  // 100ms
            }

            capturer.Stop();
        } else {
            std::cerr << "SnackaCaptureLinux: Failed to initialize X11 capture\n";
        }
    }

    if (!captureStarted) {
        return 1;
    }

    // Stop encoder
    if (encoder) {
        encoder->Stop();
    }

    std::cerr << "SnackaCaptureLinux: Capture stopped (frames: " << frameCount
              << ", encoded: " << encodedFrameCount << ")\n";

    return 0;
}

int main(int argc, char* argv[]) {
    // Parse command line arguments
    std::vector<std::string> args(argv, argv + argc);

    // Check for help
    for (const auto& arg : args) {
        if (arg == "--help" || arg == "-h") {
            PrintUsage();
            return 0;
        }
    }

    // Check for 'list' command
    if (args.size() >= 2 && args[1] == "list") {
        bool asJson = false;
        for (size_t i = 2; i < args.size(); i++) {
            if (args[i] == "--json") {
                asJson = true;
            }
        }
        return ListSources(asJson);
    }

    // Parse capture options
    int displayIndex = 0;
    std::string cameraId;
    int width = -1;  // -1 means use default for source type
    int height = -1;
    int fps = -1;
    bool encodeH264 = false;
    int bitrateMbps = -1;

    for (size_t i = 1; i < args.size(); i++) {
        if (args[i] == "--display" && i + 1 < args.size()) {
            displayIndex = std::stoi(args[++i]);
        } else if (args[i] == "--camera" && i + 1 < args.size()) {
            cameraId = args[++i];
        } else if (args[i] == "--width" && i + 1 < args.size()) {
            width = std::stoi(args[++i]);
        } else if (args[i] == "--height" && i + 1 < args.size()) {
            height = std::stoi(args[++i]);
        } else if (args[i] == "--fps" && i + 1 < args.size()) {
            fps = std::stoi(args[++i]);
        } else if (args[i] == "--encode") {
            encodeH264 = true;
        } else if (args[i] == "--bitrate" && i + 1 < args.size()) {
            bitrateMbps = std::stoi(args[++i]);
        }
    }

    // Set defaults based on source type
    bool isCamera = !cameraId.empty();
    if (width < 0) width = isCamera ? 640 : 1920;
    if (height < 0) height = isCamera ? 480 : 1080;
    if (fps < 0) fps = isCamera ? 15 : 30;
    if (bitrateMbps < 0) bitrateMbps = isCamera ? 2 : 6;

    // Validate parameters
    if (width <= 0 || width > 4096) {
        std::cerr << "SnackaCaptureLinux: Invalid width (must be 1-4096)\n";
        return 1;
    }
    if (height <= 0 || height > 4096) {
        std::cerr << "SnackaCaptureLinux: Invalid height (must be 1-4096)\n";
        return 1;
    }
    if (fps <= 0 || fps > 120) {
        std::cerr << "SnackaCaptureLinux: Invalid fps (must be 1-120)\n";
        return 1;
    }
    if (bitrateMbps <= 0 || bitrateMbps > 100) {
        std::cerr << "SnackaCaptureLinux: Invalid bitrate (must be 1-100 Mbps)\n";
        return 1;
    }

    return Capture(displayIndex, cameraId, width, height, fps, encodeH264, bitrateMbps);
}
