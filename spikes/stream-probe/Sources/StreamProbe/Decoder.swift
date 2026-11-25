import CoreMedia
import Foundation
import VideoToolbox

/// Hardware decoder producing NV12 frames so the barcode can be read from the luma plane.
final class Decoder {
    private let codec: Codec
    private var format: CMVideoFormatDescription?
    private var session: VTDecompressionSession?
    /// (decoded pixel buffer, time decode finished)
    var onFrame: ((CVPixelBuffer, UInt64) -> Void)?
    private(set) var errors = 0

    init(codec: Codec) { self.codec = codec }

    func decode(isKeyframe: Bool, parameterSets: [Data], avcc: Data) {
        if isKeyframe && !parameterSets.isEmpty { updateFormat(parameterSets) }
        guard let format, let session else { return }

        var block: CMBlockBuffer?
        let length = avcc.count
        guard CMBlockBufferCreateWithMemoryBlock(allocator: nil, memoryBlock: nil, blockLength: length,
                blockAllocator: nil, customBlockSource: nil, offsetToData: 0, dataLength: length,
                flags: kCMBlockBufferAssureMemoryNowFlag, blockBufferOut: &block) == noErr, let block else { return }
        avcc.withUnsafeBytes { raw in
            _ = CMBlockBufferReplaceDataBytes(with: raw.baseAddress!, blockBuffer: block, offsetIntoDestination: 0, dataLength: length)
        }
        var sample: CMSampleBuffer?
        var size = length
        guard CMSampleBufferCreateReady(allocator: nil, dataBuffer: block, formatDescription: format, sampleCount: 1,
                sampleTimingEntryCount: 0, sampleTimingArray: nil, sampleSizeEntryCount: 1, sampleSizeArray: &size,
                sampleBufferOut: &sample) == noErr, let sample else { return }

        let status = VTDecompressionSessionDecodeFrame(session, sampleBuffer: sample, flags: [], infoFlagsOut: nil) {
            [weak self] status, _, image, _, _ in
            let doneNs = nowNs()
            guard let self else { return }
            if status != noErr || image == nil { self.errors += 1; return }
            self.onFrame?(image!, doneNs)
        }
        if status != noErr { errors += 1 }
    }

    private func updateFormat(_ params: [Data]) {
        var newFormat: CMVideoFormatDescription?
        let pointers = params.map { d -> UnsafePointer<UInt8> in
            let p = UnsafeMutablePointer<UInt8>.allocate(capacity: d.count)
            d.copyBytes(to: p, count: d.count)
            return UnsafePointer(p)
        }
        defer { pointers.forEach { UnsafeMutablePointer(mutating: $0).deallocate() } }
        let sizes = params.map(\.count)
        let status: OSStatus
        if codec == .h264 {
            status = CMVideoFormatDescriptionCreateFromH264ParameterSets(allocator: nil, parameterSetCount: params.count,
                parameterSetPointers: pointers, parameterSetSizes: sizes, nalUnitHeaderLength: 4, formatDescriptionOut: &newFormat)
        } else {
            status = CMVideoFormatDescriptionCreateFromHEVCParameterSets(allocator: nil, parameterSetCount: params.count,
                parameterSetPointers: pointers, parameterSetSizes: sizes, nalUnitHeaderLength: 4, extensions: nil,
                formatDescriptionOut: &newFormat)
        }
        guard status == noErr, let newFormat else { errors += 1; return }
        if let format, CMFormatDescriptionEqual(format, otherFormatDescription: newFormat), session != nil { return }
        format = newFormat
        if let session { VTDecompressionSessionInvalidate(session) }
        let attrs: [CFString: Any] = [
            kCVPixelBufferPixelFormatTypeKey: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
            kCVPixelBufferIOSurfacePropertiesKey: [:] as CFDictionary,
        ]
        let spec: [CFString: Any] = [kVTVideoDecoderSpecification_RequireHardwareAcceleratedVideoDecoder: true]
        var s: VTDecompressionSession?
        VTDecompressionSessionCreate(allocator: nil, formatDescription: newFormat, decoderSpecification: spec as CFDictionary,
            imageBufferAttributes: attrs as CFDictionary, outputCallback: nil, decompressionSessionOut: &s)
        session = s
        if let s { VTSessionSetProperty(s, key: kVTDecompressionPropertyKey_RealTime, value: kCFBooleanTrue) }
    }
}
