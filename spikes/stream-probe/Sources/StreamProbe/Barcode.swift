import CoreVideo

/// Encodes (frameId, captureNs) as black/white blocks in the luma plane of an NV12 frame.
/// Three 64-bit rows: frameId, captureNs, checksum. Survives lossy compression at sane bitrates
/// because each bit is a large flat block and is read by averaging its centre.
enum Barcode {
    static let bits = 64
    static let rows = 3
    static let magic: UInt64 = 0x534E_4143_4B41_5052 // "SNACKAPR"

    static func blockSize(forWidth width: Int) -> Int { max(8, min(32, width / (bits + 8))) }

    static func stamp(_ buffer: CVPixelBuffer, frameId: UInt64, captureNs: UInt64) {
        let width = CVPixelBufferGetWidthOfPlane(buffer, 0)
        let stride = CVPixelBufferGetBytesPerRowOfPlane(buffer, 0)
        let base = CVPixelBufferGetBaseAddressOfPlane(buffer, 0)!.assumingMemoryBound(to: UInt8.self)
        let b = blockSize(forWidth: width)
        let values = [frameId, captureNs, frameId ^ captureNs ^ magic]
        for (row, value) in values.enumerated() {
            for bit in 0..<bits {
                let on = (value >> UInt64(bits - 1 - bit)) & 1 == 1
                let luma: UInt8 = on ? 235 : 16
                let x0 = bit * b
                for y in (row * b)..<((row + 1) * b) {
                    memset(base + y * stride + x0, Int32(luma), b)
                }
            }
        }
        // Neutral chroma under the barcode keeps it crisp after 4:2:0 subsampling.
        let uvStride = CVPixelBufferGetBytesPerRowOfPlane(buffer, 1)
        let uv = CVPixelBufferGetBaseAddressOfPlane(buffer, 1)!.assumingMemoryBound(to: UInt8.self)
        for y in 0..<((rows * b) / 2) {
            memset(uv + y * uvStride, 128, bits * b)
        }
    }

    /// Returns nil when the checksum doesn't match (corrupted or not a probe frame).
    static func read(_ buffer: CVPixelBuffer) -> (frameId: UInt64, captureNs: UInt64)? {
        let width = CVPixelBufferGetWidthOfPlane(buffer, 0)
        let stride = CVPixelBufferGetBytesPerRowOfPlane(buffer, 0)
        let base = CVPixelBufferGetBaseAddressOfPlane(buffer, 0)!.assumingMemoryBound(to: UInt8.self)
        let b = blockSize(forWidth: width)
        let inset = b / 4
        var values = [UInt64](repeating: 0, count: rows)
        for row in 0..<rows {
            var value: UInt64 = 0
            for bit in 0..<bits {
                var sum = 0
                var n = 0
                for y in (row * b + inset)..<((row + 1) * b - inset) {
                    let p = base + y * stride + bit * b
                    for x in inset..<(b - inset) { sum += Int(p[x]); n += 1 }
                }
                value = (value << 1) | (sum / n > 126 ? 1 : 0)
            }
            values[row] = value
        }
        guard values[0] ^ values[1] ^ magic == values[2] else { return nil }
        return (values[0], values[1])
    }
}
