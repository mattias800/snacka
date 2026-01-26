#include "Protocol.h"
#include "SourceLister.h"
#include "DisplayCapturer.h"
#include "WindowCapturer.h"
#include "CameraCapturer.h"
#include "AudioCapturer.h"
#include "MicrophoneCapturer.h"
#include "MediaFoundationEncoder.h"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>

#include <iostream>
#include <string>
#include <vector>
#include <thread>
#include <atomic>
#include <io.h>
#include <fcntl.h>

using namespace snacka;

// Global flag for clean shutdown
std::atomic<bool> g_running{true};

// Console control handler
BOOL WINAPI ConsoleHandler(DWORD signal) {
    if (signal == CTRL_C_EVENT || signal == CTRL_BREAK_EVENT || signal == CTRL_CLOSE_EVENT) {
        std::cerr << "\nSnackaCaptureWindows: Received shutdown signal\n";
        g_running = false;
        return TRUE;
    }
    return FALSE;
}

void PrintUsage() {
    std::cerr << R"(
SnackaCaptureWindows - Screen, window, camera, and microphone capture tool for Windows

USAGE:
    SnackaCaptureWindows list [--json]
    SnackaCaptureWindows [OPTIONS]

COMMANDS:
    list              List available capture sources (displays, windows, cameras, microphones)

OPTIONS:
    --display <index>     Display index to capture (default: 0)
    --window <hwnd>       Window handle to capture
    --camera <id>         Camera device ID or index to capture
    --microphone <id>     Microphone device ID or index to capture (audio only, no video)
    --width <pixels>      Output width (default: 1920, camera: 640)
    --height <pixels>     Output height (default: 1080, camera: 480)
    --fps <rate>          Frames per second (default: 30, camera: 15)
    --audio               Capture system audio (not used with camera or microphone)
    --encode              Output H.264 encoded video (instead of raw NV12)
    --bitrate <mbps>      Encoding bitrate in Mbps (default: 6, camera: 2)
    --json                Output source list as JSON (with 'list' command)
    --help                Show this help message

EXAMPLES:
    SnackaCaptureWindows list --json
    SnackaCaptureWindows --display 0 --width 1920 --height 1080 --fps 30
    SnackaCaptureWindows --display 0 --encode --bitrate 8 --audio
    SnackaCaptureWindows --window 12345678 --audio
    SnackaCaptureWindows --camera 0 --encode --bitrate 2
    SnackaCaptureWindows --microphone 0
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

int CaptureMicrophone(const std::string& microphoneId) {
    // Set stderr to binary mode for audio output
    _setmode(_fileno(stderr), _O_BINARY);

    // Set up console handler for clean shutdown
    SetConsoleCtrlHandler(ConsoleHandler, TRUE);

    // Initialize COM
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    std::cerr << "SnackaCaptureWindows: Starting microphone capture (audio only)\n";

    uint64_t audioPacketCount = 0;

    // Write audio packets to stderr
    auto audioCallback = [&](const uint8_t* data, size_t size, uint64_t timestamp) {
        if (!g_running) return;

        size_t written = 0;
        while (written < size && g_running) {
            int result = _write(_fileno(stderr), data + written, static_cast<unsigned int>(size - written));
            if (result < 0) {
                g_running = false;
                return;
            }
            written += result;
        }

        audioPacketCount++;
    };

    // Start microphone capture
    auto capturer = std::make_unique<snacka::MicrophoneCapturer>();
    if (!capturer->Initialize(microphoneId)) {
        std::cerr << "SnackaCaptureWindows: Failed to initialize microphone capture\n";
        CoUninitialize();
        return 1;
    }

    capturer->Start(audioCallback);

    // Wait for shutdown
    while (g_running && capturer->IsRunning()) {
        Sleep(100);
    }

    capturer->Stop();

    std::cerr << "SnackaCaptureWindows: Microphone capture stopped (audio packets: " << audioPacketCount << ")\n";

    CoUninitialize();
    return 0;
}

int Capture(int displayIndex, HWND windowHandle, const std::string& cameraId, int width, int height, int fps, bool captureAudio, bool encodeH264, int bitrateMbps) {
    // Set stdout to binary mode for raw frame output
    _setmode(_fileno(stdout), _O_BINARY);
    _setmode(_fileno(stderr), _O_BINARY);

    // Set up console handler for clean shutdown
    SetConsoleCtrlHandler(ConsoleHandler, TRUE);

    // Initialize COM for audio
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    std::string sourceType = !cameraId.empty() ? "camera" : (windowHandle != nullptr ? "window" : "display");
    std::cerr << "SnackaCaptureWindows: Starting " << sourceType << " capture "
              << width << "x" << height << " @ " << fps << "fps"
              << (captureAudio ? ", audio=true" : ", audio=false")
              << (encodeH264 ? ", encode=H.264 @ " + std::to_string(bitrateMbps) + "Mbps" : ", encode=raw NV12") << "\n";

    // Frame and audio statistics
    uint64_t frameCount = 0;
    uint64_t audioPacketCount = 0;
    uint64_t encodedFrameCount = 0;

    // Initialize H.264 encoder if requested
    std::unique_ptr<MediaFoundationEncoder> encoder;
    if (encodeH264) {
        if (!MediaFoundationEncoder::IsHardwareEncoderAvailable()) {
            std::cerr << "SnackaCaptureWindows: ERROR - No H.264 encoder available. Hardware encoding is required.\n";
            CoUninitialize();
            return 1;
        }

        encoder = std::make_unique<MediaFoundationEncoder>(width, height, fps, bitrateMbps);

        // Initialize encoder (creates its own D3D device)
        if (!encoder->Initialize()) {
            std::cerr << "SnackaCaptureWindows: ERROR - Failed to initialize H.264 encoder. Encoding is required.\n";
            CoUninitialize();
            return 1;
        }

        std::cerr << "SnackaCaptureWindows: Using " << encoder->GetEncoderName() << " encoder\n";
    }

    if (encodeH264 && encoder) {
        // Set callback for encoded data
        encoder->SetCallback([&](const uint8_t* data, size_t size, bool isKeyframe) {
            if (!g_running) return;

            size_t written = 0;
            while (written < size && g_running) {
                int result = _write(_fileno(stdout), data + written, static_cast<unsigned int>(size - written));
                if (result < 0) {
                    std::cerr << "SnackaCaptureWindows: Error writing encoded frame\n";
                    g_running = false;
                    return;
                }
                written += result;
            }

            encodedFrameCount++;
            if (encodedFrameCount <= 5 || encodedFrameCount % 100 == 0) {
                std::cerr << "SnackaCaptureWindows: Encoded frame " << encodedFrameCount
                          << " (" << size << " bytes" << (isKeyframe ? ", keyframe" : "") << ")\n";
            }
        });
    }

    // Write video frames to stdout (raw NV12 or encode to H.264)
    auto videoCallback = [&](const uint8_t* data, size_t size, uint64_t timestamp) {
        if (!g_running) return;

        frameCount++;

        if (encodeH264 && encoder) {
            // Encode to H.264
            if (!encoder->EncodeNV12(data, size, static_cast<int64_t>(timestamp))) {
                if (frameCount <= 5) {
                    std::cerr << "SnackaCaptureWindows: Warning - Failed to encode frame " << frameCount << "\n";
                }
            }
        } else {
            // Output raw NV12
            size_t written = 0;
            while (written < size && g_running) {
                int result = _write(_fileno(stdout), data + written, static_cast<unsigned int>(size - written));
                if (result < 0) {
                    std::cerr << "SnackaCaptureWindows: Error writing video frame\n";
                    g_running = false;
                    return;
                }
                written += result;
            }

            if (frameCount <= 5 || frameCount % 100 == 0) {
                std::cerr << "SnackaCaptureWindows: Video frame " << frameCount
                          << " (" << width << "x" << height << " NV12, " << size << " bytes)\n";
            }
        }
    };

    // Write audio packets to stderr
    auto audioCallback = [&](const uint8_t* data, size_t size, uint64_t timestamp) {
        if (!g_running) return;

        // Audio packets include the header, write directly to stderr
        // Note: We use a separate file descriptor to avoid mixing with log messages
        size_t written = 0;
        while (written < size && g_running) {
            int result = _write(_fileno(stderr), data + written, static_cast<unsigned int>(size - written));
            if (result < 0) {
                g_running = false;
                return;
            }
            written += result;
        }

        audioPacketCount++;
        if (audioPacketCount <= 10 || audioPacketCount % 100 == 0) {
            // Log to a file instead of stderr to avoid mixing with audio data
            // For now, skip logging audio stats
        }
    };

    // Start audio capture if requested
    std::unique_ptr<AudioCapturer> audioCapturer;
    if (captureAudio) {
        audioCapturer = std::make_unique<AudioCapturer>();
        if (!audioCapturer->Initialize()) {
            std::cerr << "SnackaCaptureWindows: WARNING - Failed to initialize audio capture\n";
            audioCapturer.reset();
        } else {
            audioCapturer->Start(audioCallback);
        }
    }

    // Start video capture
    bool captureStarted = false;

    if (!cameraId.empty()) {
        // Camera capture
        auto capturer = std::make_unique<CameraCapturer>();
        if (capturer->Initialize(cameraId, width, height, fps)) {
            capturer->Start(videoCallback);
            captureStarted = true;

            // Wait for shutdown
            while (g_running && capturer->IsRunning()) {
                Sleep(100);
            }

            capturer->Stop();
        }
    } else if (windowHandle != nullptr) {
        // Window capture
        auto capturer = std::make_unique<WindowCapturer>();
        if (capturer->Initialize(windowHandle, width, height, fps)) {
            capturer->Start(videoCallback);
            captureStarted = true;

            // Wait for shutdown
            while (g_running && capturer->IsRunning()) {
                Sleep(100);
            }

            capturer->Stop();
        }
    } else {
        // Display capture
        auto capturer = std::make_unique<DisplayCapturer>();
        if (capturer->Initialize(displayIndex, width, height, fps)) {
            capturer->Start(videoCallback);
            captureStarted = true;

            // Wait for shutdown
            while (g_running && capturer->IsRunning()) {
                Sleep(100);
            }

            capturer->Stop();
        }
    }

    // Stop audio capture
    if (audioCapturer) {
        audioCapturer->Stop();
    }

    // Stop encoder
    if (encoder) {
        encoder->Stop();
    }

    if (!captureStarted) {
        std::cerr << "SnackaCaptureWindows: Failed to start capture\n";
        CoUninitialize();
        return 1;
    }

    std::cerr << "SnackaCaptureWindows: Capture stopped (frames: " << frameCount
              << ", encoded: " << encodedFrameCount
              << ", audio packets: " << audioPacketCount << ")\n";

    CoUninitialize();
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
    HWND windowHandle = nullptr;
    std::string cameraId;
    std::string microphoneId;
    int width = -1;  // -1 means use default for source type
    int height = -1;
    int fps = -1;
    bool captureAudio = false;
    bool encodeH264 = false;
    int bitrateMbps = -1;
    bool hasMicrophone = false;

    for (size_t i = 1; i < args.size(); i++) {
        if (args[i] == "--display" && i + 1 < args.size()) {
            displayIndex = std::stoi(args[++i]);
        } else if (args[i] == "--window" && i + 1 < args.size()) {
            windowHandle = reinterpret_cast<HWND>(std::stoull(args[++i]));
        } else if (args[i] == "--camera" && i + 1 < args.size()) {
            cameraId = args[++i];
        } else if (args[i] == "--microphone" && i + 1 < args.size()) {
            microphoneId = args[++i];
            hasMicrophone = true;
        } else if (args[i] == "--width" && i + 1 < args.size()) {
            width = std::stoi(args[++i]);
        } else if (args[i] == "--height" && i + 1 < args.size()) {
            height = std::stoi(args[++i]);
        } else if (args[i] == "--fps" && i + 1 < args.size()) {
            fps = std::stoi(args[++i]);
        } else if (args[i] == "--audio") {
            captureAudio = true;
        } else if (args[i] == "--encode") {
            encodeH264 = true;
        } else if (args[i] == "--bitrate" && i + 1 < args.size()) {
            bitrateMbps = std::stoi(args[++i]);
        }
    }

    // Handle microphone capture mode (audio only, no video)
    if (hasMicrophone) {
        return CaptureMicrophone(microphoneId);
    }

    // Set defaults based on source type
    bool isCamera = !cameraId.empty();
    if (width < 0) width = isCamera ? 640 : 1920;
    if (height < 0) height = isCamera ? 480 : 1080;
    if (fps < 0) fps = isCamera ? 15 : 30;
    if (bitrateMbps < 0) bitrateMbps = isCamera ? 2 : 6;

    // Validate parameters
    if (width <= 0 || width > 4096) {
        std::cerr << "SnackaCaptureWindows: Invalid width (must be 1-4096)\n";
        return 1;
    }
    if (height <= 0 || height > 4096) {
        std::cerr << "SnackaCaptureWindows: Invalid height (must be 1-4096)\n";
        return 1;
    }
    if (fps <= 0 || fps > 120) {
        std::cerr << "SnackaCaptureWindows: Invalid fps (must be 1-120)\n";
        return 1;
    }
    if (bitrateMbps <= 0 || bitrateMbps > 100) {
        std::cerr << "SnackaCaptureWindows: Invalid bitrate (must be 1-100 Mbps)\n";
        return 1;
    }

    return Capture(displayIndex, windowHandle, cameraId, width, height, fps, captureAudio, encodeH264, bitrateMbps);
}
