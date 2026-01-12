import Foundation
import VideoToolbox
import CoreMedia
import CoreVideo

/// Callback type for encoded H.264 NAL units
typealias EncodedFrameCallback = (Data, Bool) -> Void

/// Hardware H.264 encoder using VideoToolbox VTCompressionSession.
/// Outputs H.264 NAL units in AVCC format (4-byte length prefix).
class VideoToolboxEncoder {
    private var compressionSession: VTCompressionSession?
    private let width: Int32
    private let height: Int32
    private let fps: Int32
    private let bitrate: Int32
    private var frameCount: Int64 = 0
    private var onEncodedFrame: EncodedFrameCallback?

    // Thread safety for callback
    private let callbackQueue = DispatchQueue(label: "com.snacka.encoder.callback")

    init(width: Int, height: Int, fps: Int, bitrateMbps: Int = 6) {
        self.width = Int32(width)
        self.height = Int32(height)
        self.fps = Int32(fps)
        self.bitrate = Int32(bitrateMbps * 1_000_000)
    }

    deinit {
        stop()
    }

    /// Starts the encoder with the given callback for encoded frames.
    func start(onEncodedFrame: @escaping EncodedFrameCallback) throws {
        self.onEncodedFrame = onEncodedFrame

        // Create compression session
        var session: VTCompressionSession?

        // Output callback - called on encoder's internal thread
        let outputCallback: VTCompressionOutputCallback = { refcon, sourceFrameRefCon, status, infoFlags, sampleBuffer in
            guard let refcon = refcon else { return }
            let encoder = Unmanaged<VideoToolboxEncoder>.fromOpaque(refcon).takeUnretainedValue()
            encoder.handleEncodedFrame(status: status, sampleBuffer: sampleBuffer)
        }

        let encoderSpec: CFDictionary? = nil  // Let VideoToolbox choose the best encoder

        // Image buffer attributes - we'll receive NV12 from ScreenCaptureKit
        let imageBufferAttributes: [CFString: Any] = [
            kCVPixelBufferPixelFormatTypeKey: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
            kCVPixelBufferWidthKey: width,
            kCVPixelBufferHeightKey: height,
        ]

        let status = VTCompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            width: width,
            height: height,
            codecType: kCMVideoCodecType_H264,
            encoderSpecification: encoderSpec,
            imageBufferAttributes: imageBufferAttributes as CFDictionary,
            compressedDataAllocator: kCFAllocatorDefault,
            outputCallback: outputCallback,
            refcon: Unmanaged.passUnretained(self).toOpaque(),
            compressionSessionOut: &session
        )

        guard status == noErr, let session = session else {
            throw EncoderError.failedToCreateSession(status)
        }

        self.compressionSession = session

        // Configure encoder for low-latency real-time encoding
        try configureSession(session)

        // Prepare to encode
        let prepareStatus = VTCompressionSessionPrepareToEncodeFrames(session)
        guard prepareStatus == noErr else {
            throw EncoderError.failedToPrepare(prepareStatus)
        }

        fputs("VideoToolboxEncoder: Started (\(width)x\(height) @ \(fps)fps, \(bitrate/1_000_000)Mbps)\n", stderr)
    }

    private func configureSession(_ session: VTCompressionSession) throws {
        // Real-time encoding (low latency)
        var status = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        guard status == noErr else { throw EncoderError.failedToSetProperty("RealTime", status) }

        // Baseline profile for maximum compatibility
        status = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ProfileLevel, value: kVTProfileLevel_H264_Baseline_AutoLevel)
        guard status == noErr else { throw EncoderError.failedToSetProperty("ProfileLevel", status) }

        // Disable B-frames for lower latency (no frame reordering)
        status = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse)
        guard status == noErr else { throw EncoderError.failedToSetProperty("AllowFrameReordering", status) }

        // Set bitrate (average)
        status = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AverageBitRate, value: bitrate as CFNumber)
        guard status == noErr else { throw EncoderError.failedToSetProperty("AverageBitRate", status) }

        // Set max bitrate (data rate limit) - 1.5x average for burst tolerance
        let maxBitrate = Int(Double(bitrate) * 1.5)
        let dataRateLimits: [Int] = [maxBitrate, 1]  // bytes per second, seconds
        status = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_DataRateLimits, value: dataRateLimits as CFArray)
        // Ignore failure - not all encoders support this

        // Keyframe interval (GOP size) - one keyframe per second
        status = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameInterval, value: fps as CFNumber)
        guard status == noErr else { throw EncoderError.failedToSetProperty("MaxKeyFrameInterval", status) }

        // Expected frame rate hint
        status = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ExpectedFrameRate, value: fps as CFNumber)
        guard status == noErr else { throw EncoderError.failedToSetProperty("ExpectedFrameRate", status) }

        // Allow temporal compression (P-frames)
        status = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AllowTemporalCompression, value: kCFBooleanTrue)
        guard status == noErr else { throw EncoderError.failedToSetProperty("AllowTemporalCompression", status) }
    }

    /// Encodes a CVPixelBuffer frame.
    func encode(pixelBuffer: CVPixelBuffer, presentationTime: CMTime, duration: CMTime = .invalid) throws {
        guard let session = compressionSession else {
            throw EncoderError.notStarted
        }

        frameCount += 1

        // Force keyframe on first frame or periodically for recovery
        var frameProperties: CFDictionary? = nil
        if frameCount == 1 || frameCount % Int64(fps * 5) == 0 {
            // Force I-frame every 5 seconds for recovery
            frameProperties = [kVTEncodeFrameOptionKey_ForceKeyFrame: true] as CFDictionary
        }

        let status = VTCompressionSessionEncodeFrame(
            session,
            imageBuffer: pixelBuffer,
            presentationTimeStamp: presentationTime,
            duration: duration,
            frameProperties: frameProperties,
            sourceFrameRefcon: nil,
            infoFlagsOut: nil
        )

        guard status == noErr else {
            throw EncoderError.failedToEncode(status)
        }
    }

    /// Stops the encoder and releases resources.
    func stop() {
        if let session = compressionSession {
            VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
            VTCompressionSessionInvalidate(session)
            compressionSession = nil
            fputs("VideoToolboxEncoder: Stopped after \(frameCount) frames\n", stderr)
        }
    }

    // MARK: - Private

    private func handleEncodedFrame(status: OSStatus, sampleBuffer: CMSampleBuffer?) {
        guard status == noErr else {
            fputs("VideoToolboxEncoder: Encode error \(status)\n", stderr)
            return
        }

        guard let sampleBuffer = sampleBuffer else { return }

        // Check if this is a keyframe
        let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[CFString: Any]]
        let isKeyframe = attachments?.first?[kCMSampleAttachmentKey_NotSync] as? Bool != true

        // Get format description for SPS/PPS (only on keyframes)
        var nalData = Data()

        if isKeyframe, let formatDesc = CMSampleBufferGetFormatDescription(sampleBuffer) {
            // Extract SPS
            if let sps = extractParameterSet(from: formatDesc, index: 0) {
                // Write SPS NAL unit with 4-byte length prefix
                var spsLength = UInt32(sps.count).bigEndian
                nalData.append(Data(bytes: &spsLength, count: 4))
                nalData.append(sps)
            }

            // Extract PPS
            if let pps = extractParameterSet(from: formatDesc, index: 1) {
                // Write PPS NAL unit with 4-byte length prefix
                var ppsLength = UInt32(pps.count).bigEndian
                nalData.append(Data(bytes: &ppsLength, count: 4))
                nalData.append(pps)
            }
        }

        // Extract NAL units from sample buffer
        guard let dataBuffer = CMSampleBufferGetDataBuffer(sampleBuffer) else { return }

        var totalLength = 0
        var dataPointer: UnsafeMutablePointer<Int8>?
        let blockStatus = CMBlockBufferGetDataPointer(dataBuffer, atOffset: 0, lengthAtOffsetOut: nil, totalLengthOut: &totalLength, dataPointerOut: &dataPointer)

        guard blockStatus == noErr, let pointer = dataPointer else { return }

        // The data is already in AVCC format (4-byte length prefix)
        // Just copy it directly
        nalData.append(Data(bytes: pointer, count: totalLength))

        // Deliver to callback
        callbackQueue.async { [weak self] in
            self?.onEncodedFrame?(nalData, isKeyframe)
        }
    }

    private func extractParameterSet(from formatDesc: CMFormatDescription, index: Int) -> Data? {
        var parameterSetPointer: UnsafePointer<UInt8>?
        var parameterSetLength = 0
        var nalUnitHeaderLength: Int32 = 0

        let status = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
            formatDesc,
            parameterSetIndex: index,
            parameterSetPointerOut: &parameterSetPointer,
            parameterSetSizeOut: &parameterSetLength,
            parameterSetCountOut: nil,
            nalUnitHeaderLengthOut: &nalUnitHeaderLength
        )

        guard status == noErr, let pointer = parameterSetPointer else { return nil }
        return Data(bytes: pointer, count: parameterSetLength)
    }
}

// MARK: - Errors

enum EncoderError: Error, LocalizedError {
    case failedToCreateSession(OSStatus)
    case failedToPrepare(OSStatus)
    case failedToSetProperty(String, OSStatus)
    case failedToEncode(OSStatus)
    case notStarted

    var errorDescription: String? {
        switch self {
        case .failedToCreateSession(let status):
            return "Failed to create compression session: \(status)"
        case .failedToPrepare(let status):
            return "Failed to prepare encoder: \(status)"
        case .failedToSetProperty(let property, let status):
            return "Failed to set \(property): \(status)"
        case .failedToEncode(let status):
            return "Failed to encode frame: \(status)"
        case .notStarted:
            return "Encoder not started"
        }
    }
}
