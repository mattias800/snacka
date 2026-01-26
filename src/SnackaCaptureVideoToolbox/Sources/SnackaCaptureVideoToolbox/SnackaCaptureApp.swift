import ArgumentParser
import Foundation
import AppKit

@main
@available(macOS 13.0, *)
struct SnackaCaptureVideoToolbox: AsyncParsableCommand {
    static let configuration = CommandConfiguration(
        abstract: "Screen, window, and camera capture tool using ScreenCaptureKit and AVFoundation"
    )

    // MARK: - List Mode (positional argument)

    @Argument(help: "Use 'list' to enumerate available capture sources")
    var command: String?

    @Flag(name: .long, help: "Output list as JSON")
    var json = false

    @Flag(name: .long, help: "Only list microphones (faster, skips screen capture enumeration)")
    var microphonesOnly = false

    // MARK: - Capture Source Options

    @Option(name: .long, help: "Display index to capture")
    var display: Int?

    @Option(name: .long, help: "Window ID to capture")
    var window: Int?

    @Option(name: .long, help: "Application bundle ID to capture")
    var app: String?

    @Option(name: .long, help: "Camera unique ID or index to capture")
    var camera: String?

    @Option(name: .long, help: "Microphone unique ID or index to capture (audio only, no video)")
    var microphone: String?

    // MARK: - Video Settings

    @Option(name: .long, help: "Output width")
    var width: Int = 1920

    @Option(name: .long, help: "Output height")
    var height: Int = 1080

    @Option(name: .long, help: "Frames per second")
    var fps: Int = 30

    // MARK: - Audio Settings

    @Flag(name: .long, help: "Capture audio from the source")
    var audio = false

    @Flag(name: .long, inversion: .prefixedNo, help: "Exclude audio from current process (default: true)")
    var excludeSelf = true

    @Option(name: .long, help: "Bundle ID of app to exclude from audio capture")
    var excludeApp: String?

    // MARK: - Encoding Settings

    @Flag(name: .long, help: "Output H.264 encoded video (instead of raw NV12)")
    var encode = false

    @Option(name: .long, help: "Encoding bitrate in Mbps (default: 6)")
    var bitrate: Int = 6

    // MARK: - Noise Suppression

    @Flag(name: .long, inversion: .prefixedNo, help: "Enable AI noise suppression for microphone (default: true)")
    var noiseSuppression = true

    // MARK: - Validation

    func validate() throws {
        // Skip validation for list command
        if command == "list" {
            return
        }

        let sourceCount = [display != nil, window != nil, app != nil, camera != nil, microphone != nil].filter { $0 }.count
        if sourceCount > 1 {
            throw ValidationError("Specify only one of --display, --window, --app, --camera, or --microphone")
        }

        guard width > 0 && width <= 4096 else {
            throw ValidationError("Width must be between 1 and 4096")
        }
        guard height > 0 && height <= 4096 else {
            throw ValidationError("Height must be between 1 and 4096")
        }
        guard fps > 0 && fps <= 120 else {
            throw ValidationError("FPS must be between 1 and 120")
        }
    }

    // MARK: - Run

    func run() async throws {
        // CRITICAL: Set activation policy FIRST to prevent dock icon
        // This marks the app as a background helper that shouldn't appear in the dock
        await MainActor.run {
            NSApplication.shared.setActivationPolicy(.accessory)
        }

        // Handle list command
        if command == "list" {
            try await runList()
            return
        }

        // Run capture
        try await runCapture()
    }

    // MARK: - List Sources

    private func runList() async throws {
        // Fast path for microphones only - skips slow SCShareableContent enumeration
        if microphonesOnly {
            let microphones = SourceLister.getAvailableMicrophones()
            if json {
                let result = ["microphones": microphones]
                let encoder = JSONEncoder()
                encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
                let data = try encoder.encode(result)
                print(String(data: data, encoding: .utf8)!)
            } else {
                print("Microphones:")
                for mic in microphones {
                    print("  [\(mic.index)] \(mic.name) - id: \(mic.id)")
                }
            }
            return
        }

        let sources = try await SourceLister.getAvailableSources()

        if json {
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            let data = try encoder.encode(sources)
            print(String(data: data, encoding: .utf8)!)
        } else {
            print("Displays:")
            for display in sources.displays {
                print("  [\(display.id)] \(display.name) (\(display.width)x\(display.height))")
            }
            print("\nWindows:")
            for window in sources.windows {
                print("  [\(window.id)] \(window.name) - \(window.appName)")
            }
            print("\nApplications:")
            for app in sources.applications {
                print("  [\(app.bundleId)] \(app.name)")
            }
            print("\nCameras:")
            for camera in sources.cameras {
                let positionStr = camera.position != "unspecified" ? " (\(camera.position))" : ""
                print("  [\(camera.index)] \(camera.name)\(positionStr) - id: \(camera.id)")
            }
            print("\nMicrophones:")
            for mic in sources.microphones {
                print("  [\(mic.index)] \(mic.name) - id: \(mic.id)")
            }
        }
    }

    // MARK: - Capture

    private func runCapture() async throws {
        // Handle microphone capture separately (audio only, no video)
        if let micId = microphone {
            fputs("SnackaCaptureVideoToolbox: Starting microphone capture (audio only, noise suppression: \(noiseSuppression))\n", stderr)

            let capturer = MicrophoneCapturer(microphoneId: micId, noiseSuppression: noiseSuppression)
            try await capturer.start()

            // Keep running until terminated
            await capturer.waitUntilDone()
            return
        }

        // Determine capture source for video modes
        let sourceType: CaptureSourceType
        if let cameraId = camera {
            sourceType = .camera(id: cameraId)
        } else if let windowId = window {
            sourceType = .window(id: windowId)
        } else if let bundleId = app {
            sourceType = .application(bundleId: bundleId)
        } else {
            sourceType = .display(index: display ?? 0)
        }

        let config = CaptureConfig(
            source: sourceType,
            width: width,
            height: height,
            fps: fps,
            captureAudio: audio,
            excludeCurrentProcessAudio: excludeSelf,
            excludeAppBundleId: excludeApp,
            encodeH264: encode,
            bitrateMbps: bitrate
        )

        // Log to stderr so it doesn't interfere with video output
        let outputFormat = encode ? "H.264 @ \(bitrate)Mbps" : "NV12"

        // Use CameraCapturer for camera source, ScreenCapturer for screen sources
        if case .camera = sourceType {
            fputs("SnackaCaptureVideoToolbox: Starting camera capture \(width)x\(height) @ \(fps)fps, output=\(outputFormat)\n", stderr)

            let capturer = CameraCapturer(config: config)
            try await capturer.start()

            // Keep running until terminated
            await capturer.waitUntilDone()
        } else {
            fputs("SnackaCaptureVideoToolbox: Starting screen capture \(width)x\(height) @ \(fps)fps, audio=\(audio), output=\(outputFormat)\n", stderr)

            let capturer = ScreenCapturer(config: config)
            try await capturer.start()

            // Keep running until terminated
            await capturer.waitUntilDone()
        }
    }
}
