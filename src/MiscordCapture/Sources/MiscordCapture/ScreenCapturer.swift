import Foundation
import ScreenCaptureKit
import CoreMedia
import CoreVideo
import AVFoundation

/// Main screen capture class using ScreenCaptureKit
class ScreenCapturer: NSObject, SCStreamDelegate, SCStreamOutput {
    private let config: CaptureConfig
    private var stream: SCStream?
    private var isRunning = false
    private let videoQueue = DispatchQueue(label: "com.miscord.capture.video", qos: .userInteractive)
    private let audioQueue = DispatchQueue(label: "com.miscord.capture.audio", qos: .userInteractive)

    // Output handles
    private let videoOutput = FileHandle.standardOutput
    private let audioOutput = FileHandle.standardError

    // Frame timing
    private var frameCount: UInt64 = 0
    private var audioSampleCount: UInt64 = 0

    // Continuation for keeping the process alive
    private var runContinuation: CheckedContinuation<Void, Never>?

    init(config: CaptureConfig) {
        self.config = config
        super.init()
    }

    func start() async throws {
        // Create content filter based on source type
        let filter: SCContentFilter
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)

        switch config.source {
        case .display(let index):
            guard index >= 0 && index < content.displays.count else {
                throw CaptureError.sourceNotFound("Display \(index) not found")
            }
            let display = content.displays[index]
            // Capture entire display, excluding nothing
            filter = SCContentFilter(display: display, excludingWindows: [])
            fputs("MiscordCapture: Capturing display \(index) (\(display.width)x\(display.height))\n", stderr)

        case .window(let id):
            guard let window = content.windows.first(where: { $0.windowID == CGWindowID(id) }) else {
                throw CaptureError.sourceNotFound("Window \(id) not found")
            }
            filter = SCContentFilter(desktopIndependentWindow: window)
            fputs("MiscordCapture: Capturing window '\(window.title ?? "Unknown")'\n", stderr)

        case .application(let bundleId):
            guard let app = content.applications.first(where: { $0.bundleIdentifier == bundleId }) else {
                throw CaptureError.sourceNotFound("Application \(bundleId) not found")
            }
            guard let display = content.displays.first else {
                throw CaptureError.sourceNotFound("No display available")
            }
            // Capture all windows of this application
            let appWindows = content.windows.filter { $0.owningApplication?.bundleIdentifier == bundleId }
            filter = SCContentFilter(display: display, including: [app], exceptingWindows: [])
            fputs("MiscordCapture: Capturing application '\(app.applicationName)' (\(appWindows.count) windows)\n", stderr)
        }

        // Configure stream
        let streamConfig = SCStreamConfiguration()

        // Video settings
        streamConfig.width = config.width
        streamConfig.height = config.height
        streamConfig.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(config.fps))
        // Use NV12 (YUV 4:2:0) - native format for H264 encoding, no conversion needed
        streamConfig.pixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        streamConfig.showsCursor = true
        fputs("MiscordCapture: Using NV12 pixel format (hardware-accelerated path)\n", stderr)

        // Audio settings
        if config.captureAudio {
            streamConfig.capturesAudio = true
            streamConfig.excludesCurrentProcessAudio = config.excludeCurrentProcessAudio
            streamConfig.sampleRate = 48000
            streamConfig.channelCount = 2
            fputs("MiscordCapture: Audio capture enabled (48kHz stereo)\n", stderr)
        }

        // Create and start stream
        stream = SCStream(filter: filter, configuration: streamConfig, delegate: self)

        try stream?.addStreamOutput(self, type: .screen, sampleHandlerQueue: videoQueue)
        if config.captureAudio {
            try stream?.addStreamOutput(self, type: .audio, sampleHandlerQueue: audioQueue)
        }

        try await stream?.startCapture()
        isRunning = true
        fputs("MiscordCapture: Capture started\n", stderr)

        // Handle termination signals
        setupSignalHandlers()
    }

    func stop() async {
        guard isRunning else { return }
        isRunning = false

        try? await stream?.stopCapture()
        stream = nil

        fputs("MiscordCapture: Capture stopped (frames: \(frameCount), audio samples: \(audioSampleCount))\n", stderr)
        runContinuation?.resume()
    }

    func waitUntilDone() async {
        await withCheckedContinuation { continuation in
            runContinuation = continuation
        }
    }

    // MARK: - SCStreamDelegate

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        fputs("MiscordCapture: Stream stopped with error: \(error.localizedDescription)\n", stderr)
        Task {
            await stop()
        }
    }

    // MARK: - SCStreamOutput

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard isRunning else { return }

        switch type {
        case .screen:
            handleVideoFrame(sampleBuffer)
        case .audio:
            handleAudioFrame(sampleBuffer)
        case .microphone:
            // We don't capture microphone through ScreenCaptureKit
            break
        @unknown default:
            break
        }
    }

    // MARK: - Frame Handling

    private func handleVideoFrame(_ sampleBuffer: CMSampleBuffer) {
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }

        // Lock the pixel buffer
        CVPixelBufferLockBaseAddress(pixelBuffer, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, .readOnly) }

        let width = CVPixelBufferGetWidth(pixelBuffer)
        let height = CVPixelBufferGetHeight(pixelBuffer)

        // NV12 is bi-planar: Y plane (full res) + UV plane (half res interleaved)
        // Total size: width * height * 1.5 bytes
        guard CVPixelBufferGetPlaneCount(pixelBuffer) == 2 else {
            fputs("MiscordCapture: Unexpected plane count: \(CVPixelBufferGetPlaneCount(pixelBuffer))\n", stderr)
            return
        }

        // Get Y plane (plane 0)
        guard let yPlaneBase = CVPixelBufferGetBaseAddressOfPlane(pixelBuffer, 0) else { return }
        let yBytesPerRow = CVPixelBufferGetBytesPerRowOfPlane(pixelBuffer, 0)

        // Get UV plane (plane 1)
        guard let uvPlaneBase = CVPixelBufferGetBaseAddressOfPlane(pixelBuffer, 1) else { return }
        let uvBytesPerRow = CVPixelBufferGetBytesPerRowOfPlane(pixelBuffer, 1)

        // Calculate output size for packed NV12 (no padding)
        let yPlaneSize = width * height
        let uvPlaneSize = width * (height / 2)
        let totalSize = yPlaneSize + uvPlaneSize

        // Pre-allocate output buffer
        var nv12Data = Data(count: totalSize)

        nv12Data.withUnsafeMutableBytes { destBuffer in
            let destPtr = destBuffer.baseAddress!.assumingMemoryBound(to: UInt8.self)
            let yPtr = yPlaneBase.assumingMemoryBound(to: UInt8.self)
            let uvPtr = uvPlaneBase.assumingMemoryBound(to: UInt8.self)

            // Copy Y plane row by row (handles stride/padding)
            for row in 0..<height {
                let srcOffset = row * yBytesPerRow
                let destOffset = row * width
                memcpy(destPtr + destOffset, yPtr + srcOffset, width)
            }

            // Copy UV plane row by row (handles stride/padding)
            let uvDestStart = yPlaneSize
            for row in 0..<(height / 2) {
                let srcOffset = row * uvBytesPerRow
                let destOffset = uvDestStart + row * width
                memcpy(destPtr + destOffset, uvPtr + srcOffset, width)
            }
        }

        // Write NV12 data to stdout - no conversion, just pass through
        do {
            try videoOutput.write(contentsOf: nv12Data)
            frameCount += 1
            if frameCount <= 5 || frameCount % 100 == 0 {
                fputs("MiscordCapture: Video frame \(frameCount) (\(width)x\(height) NV12, \(totalSize) bytes)\n", stderr)
            }
        } catch {
            fputs("MiscordCapture: Error writing video frame: \(error)\n", stderr)
        }
    }

    private func handleAudioFrame(_ sampleBuffer: CMSampleBuffer) {
        guard let dataBuffer = sampleBuffer.dataBuffer else { return }

        var length = 0
        var dataPointer: UnsafeMutablePointer<Int8>?
        CMBlockBufferGetDataPointer(dataBuffer, atOffset: 0, lengthAtOffsetOut: nil, totalLengthOut: &length, dataPointerOut: &dataPointer)

        guard let pointer = dataPointer, length > 0 else { return }

        // Get timing info
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let timestamp = UInt64(CMTimeGetSeconds(pts) * 1000)  // ms

        // Calculate sample count (16-bit stereo = 4 bytes per sample)
        let sampleCount = UInt32(length / 4)

        // Create header
        let header = AudioPacketHeader(sampleCount: sampleCount, timestamp: timestamp)

        // Write header + audio data to stderr
        // Note: We use a magic number to distinguish audio data from log messages
        do {
            try audioOutput.write(contentsOf: header.data)
            try audioOutput.write(contentsOf: Data(bytes: pointer, count: length))
            audioSampleCount += UInt64(sampleCount)
            if audioSampleCount <= 1000 || audioSampleCount % 48000 == 0 {
                fputs("MiscordCapture: Audio samples: \(audioSampleCount)\n", stderr)
            }
        } catch {
            fputs("MiscordCapture: Error writing audio: \(error)\n", stderr)
        }
    }

    // MARK: - Signal Handling

    private func setupSignalHandlers() {
        signal(SIGINT) { _ in
            fputs("\nMiscordCapture: Received SIGINT\n", stderr)
            exit(0)
        }
        signal(SIGTERM) { _ in
            fputs("\nMiscordCapture: Received SIGTERM\n", stderr)
            exit(0)
        }
    }
}
