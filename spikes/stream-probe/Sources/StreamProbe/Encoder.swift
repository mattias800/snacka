import CoreMedia
import Foundation
import VideoToolbox

enum Codec: String {
    case h264, hevc
    var cmType: CMVideoCodecType { self == .h264 ? kCMVideoCodecType_H264 : kCMVideoCodecType_HEVC }
    var wireId: UInt8 { self == .h264 ? 1 : 2 }
    init?(wireId: UInt8) {
        switch wireId { case 1: self = .h264; case 2: self = .hevc; default: return nil }
    }
}

struct EncodedFrame {
    let frameId: UInt64
    let captureNs: UInt64
    let encodedNs: UInt64
    let isKeyframe: Bool
    let parameterSets: [Data] // only on keyframes
    let avcc: Data            // 4-byte length-prefixed NAL units
}

enum EncoderError: Error { case create(OSStatus), property(String, OSStatus) }

/// Hardware encoder configured for minimum latency: no B-frames, no frame delay, real-time,
/// optionally Apple's low-latency rate control (designed for conferencing / streaming).
final class Encoder {
    let codec: Codec
    let lowLatencyRC: Bool
    private let session: VTCompressionSession
    private let fps: Int
    var onFrame: ((EncodedFrame) -> Void)?

    init(codec: Codec, width: Int, height: Int, fps: Int, bitrateMbps: Double, lowLatencyRC: Bool,
         realTime: Bool = true, quality: Double? = nil) throws {
        self.codec = codec
        self.fps = fps
        var spec: [CFString: Any] = [kVTVideoEncoderSpecification_RequireHardwareAcceleratedVideoEncoder: true]
        if lowLatencyRC { spec[kVTVideoEncoderSpecification_EnableLowLatencyRateControl] = true }
        var s: VTCompressionSession?
        var status = VTCompressionSessionCreate(
            allocator: nil, width: Int32(width), height: Int32(height), codecType: codec.cmType,
            encoderSpecification: spec as CFDictionary, imageBufferAttributes: nil,
            compressedDataAllocator: nil, outputCallback: nil, refcon: nil, compressionSessionOut: &s)
        var usedLowLatency = lowLatencyRC
        if status != noErr && lowLatencyRC {
            spec.removeValue(forKey: kVTVideoEncoderSpecification_EnableLowLatencyRateControl)
            status = VTCompressionSessionCreate(
                allocator: nil, width: Int32(width), height: Int32(height), codecType: codec.cmType,
                encoderSpecification: spec as CFDictionary, imageBufferAttributes: nil,
                compressedDataAllocator: nil, outputCallback: nil, refcon: nil, compressionSessionOut: &s)
            usedLowLatency = false
            fputs("warning: low-latency rate control unavailable for \(codec.rawValue), using default RC\n", stderr)
        }
        guard status == noErr, let session = s else { throw EncoderError.create(status) }
        self.session = session
        self.lowLatencyRC = usedLowLatency

        func set(_ key: CFString, _ value: CFTypeRef, required: Bool = false) throws {
            let st = VTSessionSetProperty(session, key: key, value: value)
            if st != noErr {
                if required { throw EncoderError.property(key as String, st) }
                fputs("note: \(key) not supported (\(st))\n", stderr)
            }
        }
        // RealTime=true lets the encoder run just fast enough for real time (may save power at the cost of latency).
        try set(kVTCompressionPropertyKey_RealTime, realTime ? kCFBooleanTrue : kCFBooleanFalse, required: true)
        try set(kVTCompressionPropertyKey_MaximizePowerEfficiency, kCFBooleanFalse)
        if let quality { try set(kVTCompressionPropertyKey_Quality, quality as CFNumber) }
        try set(kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse, required: true)
        try set(kVTCompressionPropertyKey_ProfileLevel,
                codec == .h264 ? kVTProfileLevel_H264_High_AutoLevel : kVTProfileLevel_HEVC_Main_AutoLevel)
        try set(kVTCompressionPropertyKey_AverageBitRate, Int(bitrateMbps * 1_000_000) as CFNumber)
        try set(kVTCompressionPropertyKey_ExpectedFrameRate, fps as CFNumber)
        try set(kVTCompressionPropertyKey_MaxKeyFrameInterval, (fps * 10) as CFNumber)
        try set(kVTCompressionPropertyKey_MaxFrameDelayCount, 0 as CFNumber)
        try set(kVTCompressionPropertyKey_PrioritizeEncodingSpeedOverQuality, kCFBooleanTrue)
        VTCompressionSessionPrepareToEncodeFrames(session)
    }

    func encode(_ buffer: CVPixelBuffer, frameId: UInt64, captureNs: UInt64, forceKeyframe: Bool = false) {
        let pts = CMTime(value: CMTimeValue(frameId), timescale: CMTimeScale(fps))
        let props: CFDictionary? = forceKeyframe
            ? [kVTEncodeFrameOptionKey_ForceKeyFrame: true] as CFDictionary : nil
        VTCompressionSessionEncodeFrame(
            session, imageBuffer: buffer, presentationTimeStamp: pts, duration: .invalid,
            frameProperties: props, infoFlagsOut: nil
        ) { [weak self] status, _, sample in
            guard let self, status == noErr, let sample else { return }
            let encodedNs = nowNs()
            self.onFrame?(self.package(sample, frameId: frameId, captureNs: captureNs, encodedNs: encodedNs))
        }
    }

    private func package(_ sample: CMSampleBuffer, frameId: UInt64, captureNs: UInt64, encodedNs: UInt64) -> EncodedFrame {
        var isKey = true
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false) as? [[CFString: Any]],
           let first = attachments.first, let notSync = first[kCMSampleAttachmentKey_NotSync] as? Bool {
            isKey = !notSync
        }
        var params: [Data] = []
        if isKey, let fmt = CMSampleBufferGetFormatDescription(sample) {
            var count = 0
            if codec == .h264 {
                CMVideoFormatDescriptionGetH264ParameterSetAtIndex(fmt, parameterSetIndex: 0, parameterSetPointerOut: nil,
                    parameterSetSizeOut: nil, parameterSetCountOut: &count, nalUnitHeaderLengthOut: nil)
            } else {
                CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(fmt, parameterSetIndex: 0, parameterSetPointerOut: nil,
                    parameterSetSizeOut: nil, parameterSetCountOut: &count, nalUnitHeaderLengthOut: nil)
            }
            for i in 0..<count {
                var ptr: UnsafePointer<UInt8>?
                var size = 0
                if codec == .h264 {
                    CMVideoFormatDescriptionGetH264ParameterSetAtIndex(fmt, parameterSetIndex: i, parameterSetPointerOut: &ptr,
                        parameterSetSizeOut: &size, parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil)
                } else {
                    CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(fmt, parameterSetIndex: i, parameterSetPointerOut: &ptr,
                        parameterSetSizeOut: &size, parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil)
                }
                if let ptr { params.append(Data(bytes: ptr, count: size)) }
            }
        }
        let block = CMSampleBufferGetDataBuffer(sample)!
        var length = 0
        var dataPtr: UnsafeMutablePointer<CChar>?
        CMBlockBufferGetDataPointer(block, atOffset: 0, lengthAtOffsetOut: nil, totalLengthOut: &length, dataPointerOut: &dataPtr)
        let avcc = Data(bytes: dataPtr!, count: length)
        return EncodedFrame(frameId: frameId, captureNs: captureNs, encodedNs: encodedNs,
                            isKeyframe: isKey, parameterSets: params, avcc: avcc)
    }

    func flush() { VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid) }
}
