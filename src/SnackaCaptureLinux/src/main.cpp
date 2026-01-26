#include "Protocol.h"
#include "SourceLister.h"
#include "X11Capturer.h"
#include "V4L2Capturer.h"
#include "VaapiEncoder.h"
#include "PulseAudioCapturer.h"
#include "PulseMicrophoneCapturer.h"

#include <iostream>
#include <string>
#include <vector>
#include <atomic>
#include <csignal>
#include <unistd.h>
#include <ctime>
#include <mutex>

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
SnackaCaptureLinux - Screen, camera, and microphone capture tool for Linux with VAAPI encoding

USAGE:
    SnackaCaptureLinux list [--json]
    SnackaCaptureLinux validate [--json]
    SnackaCaptureLinux [OPTIONS]

COMMANDS:
    list              List available capture sources (displays, windows, cameras, microphones)
    validate          Check hardware encoding capabilities and system compatibility

OPTIONS:
    --display <index>     Display index to capture (default: 0)
    --camera <id>         Camera device path or index to capture (e.g., /dev/video0 or 0)
    --microphone <id>     Microphone source name or index to capture (audio only, no video)
    --width <pixels>      Output width (default: 1920, camera: 640)
    --height <pixels>     Output height (default: 1080, camera: 480)
    --fps <rate>          Frames per second (default: 30, camera: 15)
    --audio               Capture system audio (via PulseAudio/PipeWire)
    --encode              Output H.264 encoded video (instead of raw NV12)
    --bitrate <mbps>      Encoding bitrate in Mbps (default: 6, camera: 2)
    --json                Output source list as JSON (with 'list' command)
    --help                Show this help message

EXAMPLES:
    SnackaCaptureLinux list --json
    SnackaCaptureLinux --display 0 --width 1920 --height 1080 --fps 30
    SnackaCaptureLinux --display 0 --encode --bitrate 8 --audio
    SnackaCaptureLinux --camera 0 --encode --bitrate 2
    SnackaCaptureLinux --camera /dev/video0 --width 640 --height 480 --fps 15
    SnackaCaptureLinux --microphone 0

OUTPUT:
    Video: H.264 NAL units in AVCC format (4-byte length prefix) to stdout
    Audio: MCAP packets (48kHz stereo 16-bit PCM) to stderr
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

// Helper to escape JSON strings
std::string EscapeJsonString(const std::string& s) {
    std::string result;
    for (char c : s) {
        switch (c) {
            case '"': result += "\\\""; break;
            case '\\': result += "\\\\"; break;
            case '\b': result += "\\b"; break;
            case '\f': result += "\\f"; break;
            case '\n': result += "\\n"; break;
            case '\r': result += "\\r"; break;
            case '\t': result += "\\t"; break;
            default:
                if (c < 0x20) {
                    char buf[8];
                    snprintf(buf, sizeof(buf), "\\u%04x", static_cast<unsigned char>(c));
                    result += buf;
                } else {
                    result += c;
                }
        }
    }
    return result;
}

int ValidateEnvironment(bool asJson) {
    auto result = VaapiEncoder::Validate();

    if (asJson) {
        // Output as JSON
        std::cout << "{\n";
        std::cout << "  \"platform\": \"" << EscapeJsonString(result.platform) << "\",\n";
        std::cout << "  \"gpuVendor\": \"" << EscapeJsonString(result.gpuVendor) << "\",\n";
        std::cout << "  \"gpuModel\": \"" << EscapeJsonString(result.gpuModel) << "\",\n";
        std::cout << "  \"driverName\": \"" << EscapeJsonString(result.driverName) << "\",\n";
        std::cout << "  \"capabilities\": {\n";
        std::cout << "    \"h264Encode\": " << (result.capabilities.h264Encode ? "true" : "false") << ",\n";
        std::cout << "    \"h264Decode\": " << (result.capabilities.h264Decode ? "true" : "false") << ",\n";
        std::cout << "    \"hevcEncode\": " << (result.capabilities.hevcEncode ? "true" : "false") << ",\n";
        std::cout << "    \"hevcDecode\": " << (result.capabilities.hevcDecode ? "true" : "false") << "\n";
        std::cout << "  },\n";
        std::cout << "  \"canCapture\": " << (result.canCapture ? "true" : "false") << ",\n";
        std::cout << "  \"canEncodeH264\": " << (result.canEncodeH264 ? "true" : "false") << ",\n";

        // Issues array
        std::cout << "  \"issues\": [\n";
        for (size_t i = 0; i < result.issues.size(); i++) {
            const auto& issue = result.issues[i];
            std::string severity;
            switch (issue.severity) {
                case IssueSeverity::Info: severity = "info"; break;
                case IssueSeverity::Warning: severity = "warning"; break;
                case IssueSeverity::Error: severity = "error"; break;
            }

            std::cout << "    {\n";
            std::cout << "      \"severity\": \"" << severity << "\",\n";
            std::cout << "      \"code\": \"" << EscapeJsonString(issue.code) << "\",\n";
            std::cout << "      \"title\": \"" << EscapeJsonString(issue.title) << "\",\n";
            std::cout << "      \"description\": \"" << EscapeJsonString(issue.description) << "\",\n";
            std::cout << "      \"suggestions\": [\n";
            for (size_t j = 0; j < issue.suggestions.size(); j++) {
                std::cout << "        \"" << EscapeJsonString(issue.suggestions[j]) << "\"";
                if (j < issue.suggestions.size() - 1) std::cout << ",";
                std::cout << "\n";
            }
            std::cout << "      ]\n";
            std::cout << "    }";
            if (i < result.issues.size() - 1) std::cout << ",";
            std::cout << "\n";
        }
        std::cout << "  ],\n";

        // Info section
        std::cout << "  \"info\": {\n";
        std::cout << "    \"drmDevice\": \"" << EscapeJsonString(result.drmDevice) << "\",\n";

        std::cout << "    \"h264Profiles\": [";
        for (size_t i = 0; i < result.h264Profiles.size(); i++) {
            std::cout << "\"" << EscapeJsonString(result.h264Profiles[i]) << "\"";
            if (i < result.h264Profiles.size() - 1) std::cout << ", ";
        }
        std::cout << "],\n";

        std::cout << "    \"h264Entrypoints\": [";
        for (size_t i = 0; i < result.h264Entrypoints.size(); i++) {
            std::cout << "\"" << EscapeJsonString(result.h264Entrypoints[i]) << "\"";
            if (i < result.h264Entrypoints.size() - 1) std::cout << ", ";
        }
        std::cout << "]\n";

        std::cout << "  }\n";
        std::cout << "}\n";
    } else {
        // Human-readable output
        std::cerr << "=== Capture Environment Validation ===\n\n";
        std::cerr << "Platform: " << result.platform << "\n";
        std::cerr << "GPU Vendor: " << result.gpuVendor << "\n";
        std::cerr << "GPU/Driver: " << result.driverName << "\n";
        std::cerr << "DRM Device: " << result.drmDevice << "\n";
        std::cerr << "\n";

        std::cerr << "Capabilities:\n";
        std::cerr << "  H.264 Encode: " << (result.capabilities.h264Encode ? "Yes" : "No") << "\n";
        std::cerr << "  H.264 Decode: " << (result.capabilities.h264Decode ? "Yes" : "No") << "\n";
        std::cerr << "\n";

        std::cerr << "Can Capture: " << (result.canCapture ? "Yes" : "No") << "\n";
        std::cerr << "Can Encode H.264: " << (result.canEncodeH264 ? "Yes" : "No") << "\n";
        std::cerr << "\n";

        if (!result.issues.empty()) {
            std::cerr << "Issues:\n";
            for (const auto& issue : result.issues) {
                std::string severityIcon;
                switch (issue.severity) {
                    case IssueSeverity::Info: severityIcon = "[INFO]"; break;
                    case IssueSeverity::Warning: severityIcon = "[WARNING]"; break;
                    case IssueSeverity::Error: severityIcon = "[ERROR]"; break;
                }
                std::cerr << "\n" << severityIcon << " " << issue.title << "\n";
                std::cerr << "  " << issue.description << "\n";
                if (!issue.suggestions.empty()) {
                    std::cerr << "  Suggestions:\n";
                    for (const auto& suggestion : issue.suggestions) {
                        std::cerr << "    - " << suggestion << "\n";
                    }
                }
            }
        }

        std::cerr << "\nH.264 Profiles: ";
        for (size_t i = 0; i < result.h264Profiles.size(); i++) {
            std::cerr << result.h264Profiles[i];
            if (i < result.h264Profiles.size() - 1) std::cerr << ", ";
        }
        std::cerr << "\n";

        std::cerr << "H.264 Entrypoints: ";
        for (size_t i = 0; i < result.h264Entrypoints.size(); i++) {
            std::cerr << result.h264Entrypoints[i];
            if (i < result.h264Entrypoints.size() - 1) std::cerr << ", ";
        }
        std::cerr << "\n";
    }

    // Return non-zero if there are errors
    for (const auto& issue : result.issues) {
        if (issue.severity == IssueSeverity::Error && issue.code != "NO_H264_ENCODE") {
            return 1;  // Critical error
        }
    }
    return 0;
}

// Mutex for stderr output (shared between video preview and audio)
std::mutex g_stderrMutex;

int CaptureMicrophone(const std::string& microphoneId) {
    // Set up signal handlers for clean shutdown
    signal(SIGINT, SignalHandler);
    signal(SIGTERM, SignalHandler);
    signal(SIGPIPE, SignalHandler);

    std::cerr << "SnackaCaptureLinux: Starting microphone capture (audio only)\n";

    uint64_t audioPacketCount = 0;

    // Audio callback - writes MCAP packets to stderr
    auto audioCallback = [&](const int16_t* data, size_t sampleCount, uint64_t timestamp) {
        if (!g_running) return;

        // Create MCAP audio packet header
        AudioPacketHeader header(static_cast<uint32_t>(sampleCount), timestamp);

        // Write header + audio data to stderr
        write(STDERR_FILENO, &header, sizeof(header));
        write(STDERR_FILENO, data, sampleCount * 4);  // 2 channels * 2 bytes

        audioPacketCount++;
        if (audioPacketCount <= 5 || audioPacketCount % 100 == 0) {
            std::cerr << "SnackaCaptureLinux: Microphone packet " << audioPacketCount
                      << " (" << sampleCount << " samples)\n";
        }
    };

    // Initialize microphone capture
    PulseMicrophoneCapturer capturer;
    if (!capturer.Initialize(microphoneId)) {
        std::cerr << "SnackaCaptureLinux: Failed to initialize microphone capture\n";
        return 1;
    }

    capturer.Start(audioCallback);

    // Wait for shutdown
    while (g_running && capturer.IsRunning()) {
        usleep(100000);  // 100ms
    }

    capturer.Stop();

    std::cerr << "SnackaCaptureLinux: Microphone capture stopped (audio packets: " << audioPacketCount << ")\n";

    return 0;
}

int Capture(int displayIndex, const std::string& cameraId, int width, int height, int fps, bool encodeH264, int bitrateMbps, bool captureAudio) {
    // Set up signal handlers for clean shutdown
    signal(SIGINT, SignalHandler);
    signal(SIGTERM, SignalHandler);
    signal(SIGPIPE, SignalHandler);

    std::string sourceType = !cameraId.empty() ? "camera" : "display";
    std::cerr << "SnackaCaptureLinux: Starting " << sourceType << " capture "
              << width << "x" << height << " @ " << fps << "fps"
              << (encodeH264 ? ", encode=H.264 @ " + std::to_string(bitrateMbps) + "Mbps" : ", encode=raw NV12")
              << (captureAudio ? ", audio=enabled" : "")
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

    // Initialize audio capture if requested
    std::unique_ptr<PulseAudioCapturer> audioCapturer;
    uint64_t audioPacketCount = 0;
    if (captureAudio) {
        audioCapturer = std::make_unique<PulseAudioCapturer>();
        if (!audioCapturer->Initialize()) {
            std::cerr << "SnackaCaptureLinux: WARNING - Failed to initialize PulseAudio, audio capture disabled\n";
            audioCapturer.reset();
        }
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

    // Audio callback - writes MCAP packets to stderr
    auto audioCallback = [&](const int16_t* data, size_t sampleCount, uint64_t timestamp) {
        if (!g_running) return;

        // Create MCAP audio packet header
        AudioPacketHeader header(static_cast<uint32_t>(sampleCount), timestamp);

        // Write header + audio data to stderr (with mutex for thread safety)
        {
            std::lock_guard<std::mutex> lock(g_stderrMutex);
            write(STDERR_FILENO, &header, sizeof(header));
            write(STDERR_FILENO, data, sampleCount * 4);  // 2 channels * 2 bytes
        }

        audioPacketCount++;
        if (audioPacketCount <= 5 || audioPacketCount % 100 == 0) {
            std::cerr << "SnackaCaptureLinux: Audio packet " << audioPacketCount
                      << " (" << sampleCount << " samples)\n";
        }
    };

    // Start audio capture if available
    if (audioCapturer) {
        audioCapturer->Start(audioCallback);
    }

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
        if (audioCapturer) {
            audioCapturer->Stop();
        }
        return 1;
    }

    // Stop encoder
    if (encoder) {
        encoder->Stop();
    }

    // Stop audio capture
    if (audioCapturer) {
        audioCapturer->Stop();
    }

    std::cerr << "SnackaCaptureLinux: Capture stopped (video frames: " << frameCount
              << ", encoded: " << encodedFrameCount
              << ", audio packets: " << audioPacketCount << ")\n";

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

    // Check for 'validate' command
    if (args.size() >= 2 && args[1] == "validate") {
        bool asJson = false;
        for (size_t i = 2; i < args.size(); i++) {
            if (args[i] == "--json") {
                asJson = true;
            }
        }
        return ValidateEnvironment(asJson);
    }

    // Parse capture options
    int displayIndex = 0;
    std::string cameraId;
    std::string microphoneId;
    bool hasMicrophone = false;
    int width = -1;  // -1 means use default for source type
    int height = -1;
    int fps = -1;
    bool encodeH264 = false;
    int bitrateMbps = -1;
    bool captureAudio = false;

    for (size_t i = 1; i < args.size(); i++) {
        if (args[i] == "--display" && i + 1 < args.size()) {
            displayIndex = std::stoi(args[++i]);
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
        } else if (args[i] == "--encode") {
            encodeH264 = true;
        } else if (args[i] == "--bitrate" && i + 1 < args.size()) {
            bitrateMbps = std::stoi(args[++i]);
        } else if (args[i] == "--audio") {
            captureAudio = true;
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

    return Capture(displayIndex, cameraId, width, height, fps, encodeH264, bitrateMbps, captureAudio);
}
