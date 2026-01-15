import Foundation
import AVFoundation
import CoreMedia
import CoreVideo
import VideoToolbox

/// Camera capture class using AVFoundation
@available(macOS 10.15, *)
class CameraCapturer: NSObject, AVCaptureVideoDataOutputSampleBufferDelegate {
    private let config: CaptureConfig
    private var captureSession: AVCaptureSession?
    private var videoOutput: AVCaptureVideoDataOutput?
    private var isRunning = false
    private let videoQueue = DispatchQueue(label: "com.snacka.camera.video", qos: .userInteractive)

    // Output handle
    private let videoOutputHandle = FileHandle.standardOutput

    // H.264 encoder (only used when config.encodeH264 is true)
    private var encoder: VideoToolboxEncoder?

    // Frame timing
    private var frameCount: UInt64 = 0
    private var encodedFrameCount: UInt64 = 0

    // Continuation for keeping the process alive
    private var runContinuation: CheckedContinuation<Void, Never>?

    init(config: CaptureConfig) {
        self.config = config
        super.init()
    }

    func start() async throws {
        // Get camera ID from config
        guard case .camera(let cameraIdOrIndex) = config.source else {
            throw CaptureError.sourceNotFound("Config source is not a camera")
        }

        // Find the camera device
        guard let device = SourceLister.findCamera(idOrIndex: cameraIdOrIndex) else {
            throw CaptureError.sourceNotFound("Camera '\(cameraIdOrIndex)' not found")
        }

        fputs("CameraCapturer: Found camera '\(device.localizedName)' (id: \(device.uniqueID))\n", stderr)

        // Create capture session
        let session = AVCaptureSession()

        // Set session preset based on requested resolution
        let preset = selectSessionPreset(width: config.width, height: config.height)
        if session.canSetSessionPreset(preset) {
            session.sessionPreset = preset
            fputs("CameraCapturer: Using session preset \(preset.rawValue)\n", stderr)
        } else {
            fputs("CameraCapturer: Warning - Cannot use preset \(preset.rawValue), using default\n", stderr)
        }

        // Add camera input
        let input: AVCaptureDeviceInput
        do {
            input = try AVCaptureDeviceInput(device: device)
        } catch {
            throw CaptureError.captureNotSupported("Cannot create input for camera: \(error.localizedDescription)")
        }

        guard session.canAddInput(input) else {
            throw CaptureError.captureNotSupported("Cannot add camera input to session")
        }
        session.addInput(input)

        // Configure video output for NV12 format (same as screen capture)
        let output = AVCaptureVideoDataOutput()
        output.videoSettings = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        ]
        output.alwaysDiscardsLateVideoFrames = true
        output.setSampleBufferDelegate(self, queue: videoQueue)

        guard session.canAddOutput(output) else {
            throw CaptureError.captureNotSupported("Cannot add video output to session")
        }
        session.addOutput(output)

        // Configure frame rate if possible
        configureFrameRate(device: device, fps: config.fps)

        self.captureSession = session
        self.videoOutput = output

        // Initialize H.264 encoder if requested
        if config.encodeH264 {
            encoder = VideoToolboxEncoder(width: config.width, height: config.height, fps: config.fps, bitrateMbps: config.bitrateMbps)
            try encoder?.start { [weak self] nalData, isKeyframe in
                self?.handleEncodedFrame(nalData: nalData, isKeyframe: isKeyframe)
            }
            fputs("CameraCapturer: H.264 encoder initialized\n", stderr)
        }

        // Start capture
        session.startRunning()
        isRunning = true

        let outputFormat = config.encodeH264 ? "H.264 @ \(config.bitrateMbps)Mbps" : "NV12"
        fputs("CameraCapturer: Started capture \(config.width)x\(config.height) @ \(config.fps)fps, output=\(outputFormat)\n", stderr)

        // Handle termination signals
        setupSignalHandlers()
    }

    func stop() {
        guard isRunning else { return }
        isRunning = false

        captureSession?.stopRunning()
        captureSession = nil
        videoOutput = nil

        // Stop encoder if running
        encoder?.stop()
        encoder = nil

        let statsMsg = config.encodeH264
            ? "frames: \(frameCount), encoded: \(encodedFrameCount)"
            : "frames: \(frameCount)"
        fputs("CameraCapturer: Stopped (\(statsMsg))\n", stderr)
        runContinuation?.resume()
    }

    func waitUntilDone() async {
        await withCheckedContinuation { continuation in
            runContinuation = continuation
        }
    }

    // MARK: - AVCaptureVideoDataOutputSampleBufferDelegate

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        guard isRunning else { return }
        handleVideoFrame(sampleBuffer)
    }

    func captureOutput(_ output: AVCaptureOutput, didDrop sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        // Frame was dropped due to late arrival
        if frameCount % 100 == 0 {
            fputs("CameraCapturer: Frame dropped (total frames: \(frameCount))\n", stderr)
        }
    }

    // MARK: - Frame Handling

    private func handleVideoFrame(_ sampleBuffer: CMSampleBuffer) {
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }

        frameCount += 1

        // If encoding is enabled, pass the pixel buffer directly to the encoder
        if config.encodeH264, let encoder = encoder {
            let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
            let duration = CMSampleBufferGetDuration(sampleBuffer)
            do {
                try encoder.encode(pixelBuffer: pixelBuffer, presentationTime: pts, duration: duration)
                if frameCount <= 5 || frameCount % 100 == 0 {
                    let width = CVPixelBufferGetWidth(pixelBuffer)
                    let height = CVPixelBufferGetHeight(pixelBuffer)
                    fputs("CameraCapturer: Video frame \(frameCount) (\(width)x\(height) -> encoder)\n", stderr)
                }
            } catch {
                fputs("CameraCapturer: Error encoding frame: \(error)\n", stderr)
            }
            return
        }

        // Raw NV12 output path (no encoding)
        CVPixelBufferLockBaseAddress(pixelBuffer, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, .readOnly) }

        let width = CVPixelBufferGetWidth(pixelBuffer)
        let height = CVPixelBufferGetHeight(pixelBuffer)

        // NV12 is bi-planar: Y plane (full res) + UV plane (half res interleaved)
        guard CVPixelBufferGetPlaneCount(pixelBuffer) == 2 else {
            fputs("CameraCapturer: Unexpected plane count: \(CVPixelBufferGetPlaneCount(pixelBuffer))\n", stderr)
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

        // Write NV12 data to stdout
        do {
            try videoOutputHandle.write(contentsOf: nv12Data)
            if frameCount <= 5 || frameCount % 100 == 0 {
                fputs("CameraCapturer: Video frame \(frameCount) (\(width)x\(height) NV12, \(totalSize) bytes)\n", stderr)
            }
        } catch {
            fputs("CameraCapturer: Error writing video frame: \(error)\n", stderr)
        }
    }

    /// Handles encoded H.264 NAL units from the VideoToolbox encoder.
    private func handleEncodedFrame(nalData: Data, isKeyframe: Bool) {
        guard isRunning else { return }

        do {
            try videoOutputHandle.write(contentsOf: nalData)
            encodedFrameCount += 1
            if encodedFrameCount <= 5 || encodedFrameCount % 100 == 0 {
                let keyframeStr = isKeyframe ? " (keyframe)" : ""
                fputs("CameraCapturer: Encoded frame \(encodedFrameCount), \(nalData.count) bytes\(keyframeStr)\n", stderr)
            }
        } catch {
            fputs("CameraCapturer: Error writing encoded frame: \(error)\n", stderr)
        }
    }

    // MARK: - Configuration Helpers

    private func selectSessionPreset(width: Int, height: Int) -> AVCaptureSession.Preset {
        // Select the best matching preset based on requested resolution
        if width >= 1920 || height >= 1080 {
            return .hd1920x1080
        } else if width >= 1280 || height >= 720 {
            return .hd1280x720
        } else if width >= 640 || height >= 480 {
            return .vga640x480
        } else {
            return .qvga320x240
        }
    }

    private func configureFrameRate(device: AVCaptureDevice, fps: Int) {
        do {
            try device.lockForConfiguration()

            // Find the best matching frame rate range
            let targetDuration = CMTime(value: 1, timescale: CMTimeScale(fps))

            for format in device.formats {
                for range in format.videoSupportedFrameRateRanges {
                    if range.minFrameDuration <= targetDuration && targetDuration <= range.maxFrameDuration {
                        device.activeFormat = format
                        device.activeVideoMinFrameDuration = targetDuration
                        device.activeVideoMaxFrameDuration = targetDuration
                        fputs("CameraCapturer: Configured frame rate to \(fps) fps\n", stderr)
                        device.unlockForConfiguration()
                        return
                    }
                }
            }

            // If exact match not found, use what we can
            if let range = device.activeFormat.videoSupportedFrameRateRanges.first {
                let clampedDuration = max(range.minFrameDuration, min(targetDuration, range.maxFrameDuration))
                device.activeVideoMinFrameDuration = clampedDuration
                device.activeVideoMaxFrameDuration = clampedDuration
                let actualFps = Int(1.0 / CMTimeGetSeconds(clampedDuration))
                fputs("CameraCapturer: Configured frame rate to \(actualFps) fps (requested \(fps))\n", stderr)
            }

            device.unlockForConfiguration()
        } catch {
            fputs("CameraCapturer: Warning - Could not configure frame rate: \(error.localizedDescription)\n", stderr)
        }
    }

    // MARK: - Signal Handling

    private func setupSignalHandlers() {
        signal(SIGINT) { _ in
            fputs("\nCameraCapturer: Received SIGINT\n", stderr)
            exit(0)
        }
        signal(SIGTERM) { _ in
            fputs("\nCameraCapturer: Received SIGTERM\n", stderr)
            exit(0)
        }
    }
}
