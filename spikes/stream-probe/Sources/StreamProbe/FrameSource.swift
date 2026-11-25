import CoreVideo
import Foundation

/// Synthetic "game-like" content: a pre-rendered texture twice the frame width, scrolled
/// horizontally every frame. Mix of gradients, hard-edged shapes and a noisy region so the
/// encoder has real work to do (static or flat content would flatter the numbers).
final class FrameSource {
    let width: Int
    let height: Int
    let pool: CVPixelBufferPool
    private let texY: [UInt8]
    private let texUV: [UInt8]
    private let texW: Int
    private var offset = 0
    private let speed: Int

    init(width: Int, height: Int, speedPxPerFrame: Int = 12) {
        self.width = width
        self.height = height
        self.speed = speedPxPerFrame & ~1 // keep chroma aligned
        texW = width * 2
        var y = [UInt8](repeating: 0, count: texW * height)
        var uv = [UInt8](repeating: 128, count: texW * (height / 2))
        var rng = SplitMix64(seed: 42)

        // Background: diagonal gradient.
        for row in 0..<height {
            for col in 0..<texW {
                y[row * texW + col] = UInt8(40 + ((row + col) * 160 / (height + texW)))
            }
        }
        // Hard-edged rectangles with random colours.
        for _ in 0..<(texW * height / 20_000) {
            let rw = Int(rng.next() % UInt64(max(8, width / 8))) + 8
            let rh = Int(rng.next() % UInt64(max(8, height / 8))) + 8
            let rx = Int(rng.next() % UInt64(texW - rw))
            let ry = Int(rng.next() % UInt64(height - rh)) & ~1
            let luma = UInt8(16 + rng.next() % 220)
            let cb = UInt8(16 + rng.next() % 224), cr = UInt8(16 + rng.next() % 224)
            for row in ry..<(ry + rh) {
                for col in rx..<(rx + rw) { y[row * texW + col] = luma }
            }
            for row in (ry / 2)..<((ry + rh) / 2) {
                var col = rx & ~1
                while col < rx + rw - 1 {
                    uv[row * texW + col] = cb
                    uv[row * texW + col + 1] = cr
                    col += 2
                }
            }
        }
        // Noisy band in the lower third (foliage/particles stand-in).
        for row in (height * 2 / 3)..<height {
            for col in 0..<texW {
                y[row * texW + col] = UInt8(truncatingIfNeeded: Int(y[row * texW + col]) / 2 + Int(rng.next() % 110))
            }
        }
        texY = y
        texUV = uv

        var p: CVPixelBufferPool?
        let attrs: [CFString: Any] = [
            kCVPixelBufferPixelFormatTypeKey: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
            kCVPixelBufferWidthKey: width,
            kCVPixelBufferHeightKey: height,
            kCVPixelBufferIOSurfacePropertiesKey: [:] as CFDictionary,
        ]
        CVPixelBufferPoolCreate(nil, [kCVPixelBufferPoolMinimumBufferCountKey: 8] as CFDictionary, attrs as CFDictionary, &p)
        pool = p!
    }

    /// Renders the next frame, stamps the barcode, and returns it with its capture timestamp.
    func next(frameId: UInt64) -> (CVPixelBuffer, UInt64) {
        var out: CVPixelBuffer?
        CVPixelBufferPoolCreatePixelBuffer(nil, pool, &out)
        let buffer = out!
        CVPixelBufferLockBaseAddress(buffer, [])
        let yStride = CVPixelBufferGetBytesPerRowOfPlane(buffer, 0)
        let uvStride = CVPixelBufferGetBytesPerRowOfPlane(buffer, 1)
        let yDst = CVPixelBufferGetBaseAddressOfPlane(buffer, 0)!.assumingMemoryBound(to: UInt8.self)
        let uvDst = CVPixelBufferGetBaseAddressOfPlane(buffer, 1)!.assumingMemoryBound(to: UInt8.self)
        texY.withUnsafeBufferPointer { src in
            for row in 0..<height {
                memcpy(yDst + row * yStride, src.baseAddress! + row * texW + offset, width)
            }
        }
        texUV.withUnsafeBufferPointer { src in
            for row in 0..<(height / 2) {
                memcpy(uvDst + row * uvStride, src.baseAddress! + row * texW + offset, width)
            }
        }
        offset = (offset + speed) % width
        let captureNs = nowNs()
        Barcode.stamp(buffer, frameId: frameId, captureNs: captureNs)
        CVPixelBufferUnlockBaseAddress(buffer, [])
        return (buffer, captureNs)
    }
}

struct SplitMix64 {
    var state: UInt64
    init(seed: UInt64) { state = seed }
    mutating func next() -> UInt64 {
        state &+= 0x9E37_79B9_7F4A_7C15
        var z = state
        z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
        z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
        return z ^ (z >> 31)
    }
}
