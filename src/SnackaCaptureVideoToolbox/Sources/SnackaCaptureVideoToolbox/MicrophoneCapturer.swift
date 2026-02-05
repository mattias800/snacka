import Foundation
import AVFoundation
import CoreMedia
import CoreAudio
import CRNNoise

/// Microphone capture class using AVFoundation
/// Captures audio from a microphone and outputs 48kHz 16-bit stereo PCM via MCAP protocol to stderr
@available(macOS 10.15, *)
class MicrophoneCapturer: NSObject, AVCaptureAudioDataOutputSampleBufferDelegate {
    private let microphoneId: String
    private let noiseSuppressionEnabled: Bool
    private var captureSession: AVCaptureSession?
    private var audioOutput: AVCaptureAudioDataOutput?
    private var isRunning = false
    private let audioQueue = DispatchQueue(label: "com.snacka.microphone.audio", qos: .userInteractive)

    // Output handle
    private let stderrHandle = FileHandle.standardError

    // Audio stats
    private var sampleCount: UInt64 = 0

    // Audio format info (detected from first audio frame)
    private var audioSampleRate: UInt32 = 48000
    private var audioBitsPerSample: UInt8 = 16
    private var audioChannels: UInt8 = 2
    private var audioIsFloat: Bool = false
    private var audioIsInterleaved: Bool = true
    private var audioFormatDetected: Bool = false

    // RNNoise state for noise suppression (one per channel)
    private var rnnoiseStateLeft: OpaquePointer?
    private var rnnoiseStateRight: OpaquePointer?

    // Frame size: 480 samples per frame (10ms at 48kHz)
    // Used for both RNNoise processing and consistent output framing
    private let frameSize = 480
    private var leftBuffer: [Float] = []
    private var rightBuffer: [Float] = []

    // Buffer for non-RNNoise output (still need consistent frame sizes for MCAP)
    private var outputBuffer: Data = Data()

    // Continuation for keeping the process alive
    private var runContinuation: CheckedContinuation<Void, Never>?

    init(microphoneId: String, noiseSuppression: Bool = true) {
        self.microphoneId = microphoneId
        self.noiseSuppressionEnabled = noiseSuppression
        super.init()

        if noiseSuppressionEnabled {
            rnnoiseStateLeft = rnnoise_create(nil)
            rnnoiseStateRight = rnnoise_create(nil)
            fputs("MicrophoneCapturer: RNNoise noise suppression enabled\n", stderr)
        }
    }

    deinit {
        if let state = rnnoiseStateLeft {
            rnnoise_destroy(state)
        }
        if let state = rnnoiseStateRight {
            rnnoise_destroy(state)
        }
    }

    func start() async throws {
        // Find the microphone device
        guard let device = SourceLister.findMicrophone(idOrIndex: microphoneId) else {
            throw CaptureError.sourceNotFound("Microphone '\(microphoneId)' not found")
        }

        fputs("MicrophoneCapturer: Found microphone '\(device.localizedName)' (id: \(device.uniqueID))\n", stderr)

        // Create capture session
        let session = AVCaptureSession()

        // Add microphone input
        let input: AVCaptureDeviceInput
        do {
            input = try AVCaptureDeviceInput(device: device)
        } catch {
            throw CaptureError.captureNotSupported("Cannot create input for microphone: \(error.localizedDescription)")
        }

        guard session.canAddInput(input) else {
            throw CaptureError.captureNotSupported("Cannot add microphone input to session")
        }
        session.addInput(input)

        // Configure audio output
        let output = AVCaptureAudioDataOutput()
        output.setSampleBufferDelegate(self, queue: audioQueue)

        // Request 48kHz 16-bit stereo output format
        let audioSettings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: 48000,
            AVNumberOfChannelsKey: 2,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsNonInterleaved: false
        ]
        output.audioSettings = audioSettings

        guard session.canAddOutput(output) else {
            throw CaptureError.captureNotSupported("Cannot add audio output to session")
        }
        session.addOutput(output)

        self.captureSession = session
        self.audioOutput = output

        // Start capture
        session.startRunning()
        isRunning = true

        fputs("MicrophoneCapturer: Started capture (requesting 48kHz 16-bit stereo)\n", stderr)

        // Handle termination signals
        setupSignalHandlers()
    }

    func stop() {
        guard isRunning else { return }
        isRunning = false

        captureSession?.stopRunning()
        captureSession = nil
        audioOutput = nil

        fputs("MicrophoneCapturer: Stopped (samples: \(sampleCount))\n", stderr)
        runContinuation?.resume()
    }

    func waitUntilDone() async {
        await withCheckedContinuation { continuation in
            runContinuation = continuation
        }
    }

    // MARK: - AVCaptureAudioDataOutputSampleBufferDelegate

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        guard isRunning else { return }
        handleAudioFrame(sampleBuffer)
    }

    // MARK: - Audio Handling

    private func handleAudioFrame(_ sampleBuffer: CMSampleBuffer) {
        guard let dataBuffer = sampleBuffer.dataBuffer else { return }

        // Detect audio format from the first frame
        if !audioFormatDetected {
            detectAudioFormat(from: sampleBuffer)
        }

        var length = 0
        var dataPointer: UnsafeMutablePointer<Int8>?
        CMBlockBufferGetDataPointer(dataBuffer, atOffset: 0, lengthAtOffsetOut: nil, totalLengthOut: &length, dataPointerOut: &dataPointer)

        guard let pointer = dataPointer, length > 0 else { return }

        // Get timing info
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let timestamp = UInt64(CMTimeGetSeconds(pts) * 1000)  // ms

        // Convert to normalized format: 48kHz 16-bit stereo
        var normalizedData = normalizeAudio(
            pointer: pointer,
            length: length,
            isFloat: audioIsFloat,
            bitsPerSample: Int(audioBitsPerSample),
            channels: Int(audioChannels),
            inputSampleRate: Int(audioSampleRate)
        )

        guard !normalizedData.isEmpty else { return }

        // Apply RNNoise noise suppression if enabled, otherwise just buffer for consistent frame sizes
        // Both paths output complete 480-sample frames for MCAP consistency
        if noiseSuppressionEnabled {
            normalizedData = processWithRNNoise(normalizedData)
        } else {
            normalizedData = bufferToFrames(normalizedData)
        }
        guard !normalizedData.isEmpty else { return }

        // Output is always 48kHz 16-bit stereo = 4 bytes per frame
        let outputFrameCount = UInt32(normalizedData.count / 4)

        // Create header - always output as 48kHz 16-bit stereo
        let header = AudioPacketHeader(
            sampleCount: outputFrameCount,
            timestamp: timestamp,
            sampleRate: 48000,
            bitsPerSample: 16,
            channels: 2,
            isFloat: 0
        )

        // Write header + normalized audio data to stderr
        do {
            try stderrHandle.write(contentsOf: header.data)
            try stderrHandle.write(contentsOf: normalizedData)
            sampleCount += UInt64(outputFrameCount)
            if sampleCount <= 1000 || sampleCount % 48000 == 0 {
                fputs("MicrophoneCapturer: Audio samples: \(sampleCount)\n", stderr)
            }
        } catch {
            fputs("MicrophoneCapturer: Error writing audio: \(error)\n", stderr)
        }
    }

    /// Buffer audio into consistent 480-sample frames (10ms at 48kHz)
    /// This ensures MCAP packet sizes are predictable even without RNNoise
    private func bufferToFrames(_ data: Data) -> Data {
        // Add new data to output buffer
        outputBuffer.append(data)

        // Calculate bytes per frame: 480 samples * 2 channels * 2 bytes = 1920 bytes
        let bytesPerFrame = frameSize * 4

        // Only output complete frames
        let completeFrames = outputBuffer.count / bytesPerFrame
        guard completeFrames > 0 else { return Data() }

        let outputBytes = completeFrames * bytesPerFrame
        let result = outputBuffer.prefix(outputBytes)

        // Keep remainder for next call
        outputBuffer = Data(outputBuffer.suffix(from: outputBytes))

        return Data(result)
    }

    /// Process audio through RNNoise for noise suppression
    /// RNNoise requires 480-sample frames (10ms at 48kHz)
    private func processWithRNNoise(_ data: Data) -> Data {
        guard let stateLeft = rnnoiseStateLeft, let stateRight = rnnoiseStateRight else {
            return data
        }

        // Convert Int16 stereo to separate float channels
        let frameCount = data.count / 4  // 4 bytes per stereo frame (2 channels x 2 bytes)

        data.withUnsafeBytes { rawBuffer in
            let int16Ptr = rawBuffer.bindMemory(to: Int16.self)
            for i in 0..<frameCount {
                // RNNoise expects float values in range -32768 to 32767
                leftBuffer.append(Float(int16Ptr[i * 2]))
                rightBuffer.append(Float(int16Ptr[i * 2 + 1]))
            }
        }

        // Process complete 480-sample frames
        var outputData = Data()

        while leftBuffer.count >= frameSize && rightBuffer.count >= frameSize {
            // Extract frames for processing
            var leftFrame = Array(leftBuffer.prefix(frameSize))
            var rightFrame = Array(rightBuffer.prefix(frameSize))

            // Process through RNNoise
            var processedLeft = [Float](repeating: 0, count: frameSize)
            var processedRight = [Float](repeating: 0, count: frameSize)

            leftFrame.withUnsafeMutableBufferPointer { inPtr in
                processedLeft.withUnsafeMutableBufferPointer { outPtr in
                    _ = rnnoise_process_frame(stateLeft, outPtr.baseAddress, inPtr.baseAddress)
                }
            }

            rightFrame.withUnsafeMutableBufferPointer { inPtr in
                processedRight.withUnsafeMutableBufferPointer { outPtr in
                    _ = rnnoise_process_frame(stateRight, outPtr.baseAddress, inPtr.baseAddress)
                }
            }

            // Convert back to Int16 stereo and append to output
            for i in 0..<frameSize {
                let leftSample = Int16(clamping: Int(processedLeft[i]))
                let rightSample = Int16(clamping: Int(processedRight[i]))

                var stereoFrame = [leftSample, rightSample]
                outputData.append(contentsOf: Data(bytes: &stereoFrame, count: 4))
            }

            // Remove processed samples from buffers
            leftBuffer.removeFirst(frameSize)
            rightBuffer.removeFirst(frameSize)
        }

        return outputData
    }

    private var normalizeLogCount = 0

    /// Converts any input audio format to 48kHz 16-bit stereo
    private func normalizeAudio(pointer: UnsafeMutablePointer<Int8>, length: Int, isFloat: Bool, bitsPerSample: Int, channels: Int, inputSampleRate: Int) -> Data {
        let bytesPerSample = bitsPerSample / 8

        let frameCount: Int
        let planarPlaneSize: Int

        if audioIsInterleaved {
            let bytesPerFrame = bytesPerSample * channels
            frameCount = length / bytesPerFrame
            planarPlaneSize = 0
        } else {
            planarPlaneSize = length / channels
            frameCount = planarPlaneSize / bytesPerSample
        }

        // Log diagnostics for first few calls
        normalizeLogCount += 1
        if normalizeLogCount <= 5 {
            fputs("MicrophoneCapturer: normalizeAudio #\(normalizeLogCount): length=\(length), bitsPerSample=\(bitsPerSample), channels=\(channels), frameCount=\(frameCount)\n", stderr)
        }

        guard frameCount > 0 else { return Data() }

        // If input is already 48kHz, no resampling needed
        // If input is different sample rate, we need to resample
        let needsResampling = inputSampleRate != 48000

        // Calculate output frame count (after potential resampling)
        let outputFrameCount: Int
        if needsResampling {
            outputFrameCount = (frameCount * 48000) / inputSampleRate
        } else {
            outputFrameCount = frameCount
        }

        guard outputFrameCount > 0 else { return Data() }

        // Output: 16-bit stereo = 4 bytes per frame
        var output = Data(count: outputFrameCount * 4)

        let rawPtr = UnsafeRawPointer(pointer)

        output.withUnsafeMutableBytes { outBuffer in
            let outPtr = outBuffer.baseAddress!.assumingMemoryBound(to: Int16.self)

            for outIdx in 0..<outputFrameCount {
                // Calculate input frame index (with resampling if needed)
                let inIdx: Int
                if needsResampling {
                    inIdx = (outIdx * inputSampleRate) / 48000
                } else {
                    inIdx = outIdx
                }

                guard inIdx < frameCount else { break }

                var leftSum: Float = 0
                var rightSum: Float = 0

                for ch in 0..<channels {
                    let chOffset: Int
                    if audioIsInterleaved {
                        chOffset = inIdx * bytesPerSample * channels + ch * bytesPerSample
                    } else {
                        chOffset = ch * planarPlaneSize + inIdx * bytesPerSample
                    }

                    var sample: Float = 0

                    if isFloat && bitsPerSample == 32 {
                        sample = rawPtr.load(fromByteOffset: chOffset, as: Float.self)
                    } else if bitsPerSample == 16 {
                        let intSample = rawPtr.load(fromByteOffset: chOffset, as: Int16.self)
                        sample = Float(intSample) / 32768.0
                    } else if bitsPerSample == 32 && !isFloat {
                        let intSample = rawPtr.load(fromByteOffset: chOffset, as: Int32.self)
                        sample = Float(intSample) / 2147483648.0
                    } else if bitsPerSample == 24 {
                        let b0 = rawPtr.load(fromByteOffset: chOffset, as: UInt8.self)
                        let b1 = rawPtr.load(fromByteOffset: chOffset + 1, as: UInt8.self)
                        let b2 = rawPtr.load(fromByteOffset: chOffset + 2, as: UInt8.self)
                        let intSample = (Int32(b2) << 24) | (Int32(b1) << 16) | (Int32(b0) << 8)
                        sample = Float(intSample) / 2147483648.0
                    }

                    // Simple stereo mapping: even channels -> left, odd channels -> right
                    if ch % 2 == 0 {
                        leftSum += sample
                    } else {
                        rightSum += sample
                    }
                }

                // Average channels per side
                let leftChannels = (channels + 1) / 2
                let rightChannels = channels / 2
                let left = leftSum / Float(max(1, leftChannels))
                let right = rightChannels > 0 ? rightSum / Float(rightChannels) : left

                // Convert to Int16 and write stereo output
                outPtr[outIdx * 2] = Int16(clamping: Int(left * 32767))
                outPtr[outIdx * 2 + 1] = Int16(clamping: Int(right * 32767))
            }
        }

        return output
    }

    private func detectAudioFormat(from sampleBuffer: CMSampleBuffer) {
        guard let formatDesc = CMSampleBufferGetFormatDescription(sampleBuffer) else {
            fputs("MicrophoneCapturer: ERROR - No audio format description available\n", stderr)
            return
        }

        guard let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(formatDesc)?.pointee else {
            fputs("MicrophoneCapturer: ERROR - Could not get ASBD from format description\n", stderr)
            return
        }

        audioSampleRate = UInt32(asbd.mSampleRate)
        audioChannels = UInt8(asbd.mChannelsPerFrame)
        audioIsFloat = (asbd.mFormatFlags & kAudioFormatFlagIsFloat) != 0
        audioIsInterleaved = (asbd.mFormatFlags & kAudioFormatFlagIsNonInterleaved) == 0
        audioBitsPerSample = UInt8(asbd.mBitsPerChannel)

        audioFormatDetected = true

        fputs("MicrophoneCapturer: Audio format detected - \(audioSampleRate)Hz, \(audioBitsPerSample)-bit, \(audioChannels) ch, \(audioIsFloat ? "float" : "int")\n", stderr)

        if audioSampleRate != 48000 {
            fputs("MicrophoneCapturer: Will resample from \(audioSampleRate)Hz to 48000Hz\n", stderr)
        }
    }

    // MARK: - Signal Handling

    private func setupSignalHandlers() {
        signal(SIGINT) { _ in
            fputs("\nMicrophoneCapturer: Received SIGINT\n", stderr)
            exit(0)
        }
        signal(SIGTERM) { _ in
            fputs("\nMicrophoneCapturer: Received SIGTERM\n", stderr)
            exit(0)
        }
    }
}
