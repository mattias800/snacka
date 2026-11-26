import AppKit
import CoreMedia
import CoreVideo
import ScreenCaptureKit

// Real screen capture (#49): ScreenCaptureKit -> VideoToolbox -> UDP -> VideoToolbox -> barcode.
//
// A small always-on-top window at the display's top-left corner shows the barcode (frame number +
// the time it was drawn), redrawn every display refresh. ScreenCaptureKit captures that corner of
// the display 1:1 in NV12, the sender reads the barcode back from the captured pixels, and the
// frame takes the same encode -> wire -> decode path as `loop`. "total" therefore runs from the
// moment the app drew the frame to the decoded picture: window server composition, capture,
// encode, network and decode. Only the viewer's own scanout is left out.
//
// Needs Screen Recording permission for the terminal running it.

/// The always-on-top barcode, redrawn on every display refresh.
final class BarcodeWindow: NSObject {
    private let window: NSWindow
    private let layer: CALayer
    private let blockPx: Int
    private let context: CGContext
    private var link: CVDisplayLink?
    private var caLink: AnyObject?
    private var frameId: UInt64 = 0
    /// Moved along with each drawing, so both change in the same frame.
    var motion: MotionWindow?

    init(blockPx: Int) {
        self.blockPx = blockPx
        let screen = NSScreen.main!
        let scale = screen.backingScaleFactor
        let widthPx = Barcode.bits * blockPx
        let heightPx = Barcode.rows * blockPx
        let size = NSSize(width: CGFloat(widthPx) / scale, height: CGFloat(heightPx) / scale)
        // AppKit's origin is bottom-left; the capture (and the barcode reader) start top-left.
        window = NSWindow(contentRect: NSRect(origin: NSPoint(x: screen.frame.minX, y: screen.frame.maxY - size.height), size: size),
                          styleMask: .borderless, backing: .buffered, defer: false)
        window.level = .screenSaver // above the menu bar
        window.isOpaque = true
        window.hasShadow = false
        window.ignoresMouseEvents = true
        window.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle]
        let view = NSView(frame: NSRect(origin: .zero, size: size))
        view.wantsLayer = true
        window.contentView = view
        layer = view.layer!
        layer.magnificationFilter = .nearest
        layer.contentsGravity = .resize
        context = CGContext(data: nil, width: widthPx, height: heightPx, bitsPerComponent: 8, bytesPerRow: widthPx,
                            space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGImageAlphaInfo.none.rawValue)!
        super.init()
        window.orderFrontRegardless()
    }

    /// Redraws on every refresh at up to `fps`. A display link asking for 120 Hz is what makes a
    /// ProMotion display run at 120 Hz (as a game would); the older CVDisplayLink got 60.
    func start(fps: Int) {
        if #available(macOS 14.0, *) {
            let displayLink = window.contentView!.displayLink(target: self, selector: #selector(tick))
            let rate = Float(fps)
            displayLink.preferredFrameRateRange = CAFrameRateRange(minimum: rate, maximum: rate, preferred: rate)
            displayLink.add(to: .main, forMode: .common)
            caLink = displayLink
        } else {
            CVDisplayLinkCreateWithActiveCGDisplays(&link)
            CVDisplayLinkSetOutputHandler(link!) { [weak self] _, _, _, _, _ in
                DispatchQueue.main.async { self?.draw() }
                return kCVReturnSuccess
            }
            CVDisplayLinkStart(link!)
        }
    }

    @objc private func tick() { draw() }

    /// Stamps the next frame number and "now" and hands the picture to the window server.
    private func draw() {
        frameId += 1
        let drawnNs = nowNs()
        let base = context.data!.assumingMemoryBound(to: UInt8.self)
        let values = [frameId, drawnNs, frameId ^ drawnNs ^ Barcode.magic]
        for (row, value) in values.enumerated() {
            for bit in 0..<Barcode.bits {
                let on = (value >> UInt64(Barcode.bits - 1 - bit)) & 1 == 1
                for y in (row * blockPx)..<((row + 1) * blockPx) {
                    memset(base + y * context.bytesPerRow + bit * blockPx, on ? 255 : 0, blockPx)
                }
            }
        }
        motion?.step()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        layer.contents = context.makeImage()
        CATransaction.commit()
    }
}

/// Game-like motion under the barcode (`--motion`): the synthetic scroll texture (gradient, hard
/// shapes, a noisy band) filling the captured region and moving 12 px per frame. Scrolled by the
/// window server on the GPU (contentsRect), so it costs no CPU and loads the encoder like a game.
final class MotionWindow: NSObject {
    private let window: NSWindow
    private let layer: CALayer
    private let widthPx: Int
    private var offset = 0

    init(widthPx: Int, heightPx: Int) {
        self.widthPx = widthPx
        let screen = NSScreen.main!
        let scale = screen.backingScaleFactor
        let size = NSSize(width: CGFloat(widthPx) / scale, height: CGFloat(heightPx) / scale)
        window = NSWindow(contentRect: NSRect(origin: NSPoint(x: screen.frame.minX, y: screen.frame.maxY - size.height), size: size),
                          styleMask: .borderless, backing: .buffered, defer: false)
        window.level = NSWindow.Level(rawValue: NSWindow.Level.screenSaver.rawValue - 1)
        window.isOpaque = true
        window.hasShadow = false
        window.ignoresMouseEvents = true
        window.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle]
        let view = NSView(frame: NSRect(origin: .zero, size: size))
        view.wantsLayer = true
        window.contentView = view
        layer = view.layer!
        layer.contentsGravity = .resize
        layer.contents = MotionWindow.texture(width: widthPx * 2, height: heightPx)
        layer.contentsRect = CGRect(x: 0, y: 0, width: 0.5, height: 1)
        super.init()
        window.orderFrontRegardless()
    }

    func step() {
        offset = (offset + 12) % widthPx
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        layer.contentsRect = CGRect(x: CGFloat(offset) / CGFloat(widthPx * 2), y: 0, width: 0.5, height: 1)
        CATransaction.commit()
    }

    private static func texture(width: Int, height: Int) -> CGImage {
        var px = [UInt8](repeating: 0, count: width * height * 4)
        var rng = SplitMix64(seed: 42)
        for row in 0..<height {
            for col in 0..<width {
                let g = UInt8(40 + (row + col) * 160 / (height + width))
                let i = (row * width + col) * 4
                px[i] = g; px[i + 1] = g &- 10; px[i + 2] = g / 2 + 40; px[i + 3] = 255
            }
        }
        for _ in 0..<(width * height / 20_000) {
            let rw = Int(rng.next() % UInt64(max(8, width / 16))) + 8
            let rh = Int(rng.next() % UInt64(max(8, height / 8))) + 8
            let rx = Int(rng.next() % UInt64(width - rw)), ry = Int(rng.next() % UInt64(height - rh))
            let c = [UInt8(16 + rng.next() % 224), UInt8(16 + rng.next() % 224), UInt8(16 + rng.next() % 224)]
            for row in ry..<(ry + rh) {
                for col in rx..<(rx + rw) {
                    let i = (row * width + col) * 4
                    px[i] = c[0]; px[i + 1] = c[1]; px[i + 2] = c[2]
                }
            }
        }
        for row in (height * 2 / 3)..<height {
            for col in 0..<width {
                let n = UInt8(rng.next() % 110), i = (row * width + col) * 4
                for k in 0..<3 { px[i + k] = px[i + k] / 2 &+ n }
            }
        }
        let provider = CGDataProvider(data: Data(px) as CFData)!
        return CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
                       space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipLast.rawValue),
                       provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)!
    }
}

/// The top-left `width`x`height` pixels of the main display, as NV12, at up to `fps`.
final class ScreenSource: NSObject, SCStreamOutput, SCStreamDelegate {
    var onFrame: ((CVPixelBuffer, UInt64) -> Void)?
    private var stream: SCStream?
    private let queue = DispatchQueue(label: "capture", qos: .userInteractive)

    func start(width: Int, height: Int, fps: Int) async throws {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first(where: { $0.displayID == CGMainDisplayID() }) else {
            throw NSError(domain: "stream-probe", code: 1, userInfo: [NSLocalizedDescriptionKey: "main display not found"])
        }
        let scale = NSScreen.main!.backingScaleFactor
        let config = SCStreamConfiguration()
        config.width = width
        config.height = height
        // In points; with width/height in pixels this maps the region 1:1 onto the output.
        config.sourceRect = CGRect(x: 0, y: 0, width: CGFloat(width) / scale, height: CGFloat(height) / scale)
        config.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(fps))
        config.pixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange // what the encoder wants: no conversion
        config.queueDepth = 4
        config.showsCursor = false
        let stream = SCStream(filter: SCContentFilter(display: display, excludingWindows: []), configuration: config, delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        try await stream.startCapture()
        self.stream = stream
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        let arrivedNs = nowNs()
        guard type == .screen, sampleBuffer.isValid,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let raw = attachments.first?[.status] as? Int, SCFrameStatus(rawValue: raw) == .complete,
              let buffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        onFrame?(buffer, arrivedNs)
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        fputs("capture stopped: \(error.localizedDescription)\n", stderr)
    }
}

/// `screen` mode: capture this Mac's screen and loop it through encode -> UDP -> decode.
func runScreenLoop() -> Never {
    let app = NSApplication.shared
    app.setActivationPolicy(.accessory)

    let rx = ReceiverPipeline(port: port)
    rx.start()
    let encoder: Encoder
    do {
        encoder = try Encoder(codec: codec, width: width, height: height, fps: fps, bitrateMbps: bitrate,
                              lowLatencyRC: lowLatency, realTime: realTime, quality: quality)
    } catch {
        fputs("encoder: \(error)\n", stderr)
        exit(1)
    }
    config["lowLatencyRC"] = encoder.lowLatencyRC
    config["source"] = "screen"
    let sender = UdpSender(host: "127.0.0.1", port: port)
    // "encode" (from main.swift's receiver) runs from the drawing; encodeOnly from the captured frame.
    let arrivals = NSLock()
    var arrivedAt: [UInt64: UInt64] = [:]
    encoder.onFrame = { frame in
        sender.send(frame, codec: codec)
        stats.count("sentFrames")
        arrivals.lock()
        let arrived = arrivedAt.removeValue(forKey: frame.frameId)
        arrivals.unlock()
        if let arrived { stats.add("encodeOnly", frame.encodedNs - arrived) }
    }

    let stamp = BarcodeWindow(blockPx: Barcode.blockSize(forWidth: width))
    if flag("motion") {
        stamp.motion = MotionWindow(widthPx: width, heightPx: height)
        config["motion"] = true
    }
    let source = ScreenSource()
    var lastFrameId: UInt64 = 0
    var unreadable: UInt64 = 0
    source.onFrame = { buffer, arrivedNs in
        CVPixelBufferLockBaseAddress(buffer, .readOnly)
        let code = Barcode.read(buffer)
        CVPixelBufferUnlockBaseAddress(buffer, .readOnly)
        guard let code else {
            stats.count("captureUnreadable")
            unreadable += 1
            return
        }
        // The window server re-sends unchanged pictures now and then; one frame per drawing.
        guard code.frameId != lastFrameId else { stats.count("captureRepeat"); return }
        stats.add("capture", arrivedNs - code.captureNs)
        let forceKey = lastFrameId == 0 || sender.takeKeyframeRequest()
        lastFrameId = code.frameId
        arrivals.lock()
        arrivedAt[code.frameId] = arrivedNs
        arrivals.unlock()
        encoder.encode(buffer, frameId: code.frameId, captureNs: code.captureNs, forceKeyframe: forceKey)
    }

    stamp.start(fps: fps)
    Task {
        do {
            try await source.start(width: width, height: height, fps: fps)
        } catch {
            fputs("screen capture failed (Screen Recording permission?): \(error.localizedDescription)\n", stderr)
            exit(1)
        }
    }
    Thread {
        let begin = nowNs()
        Thread { sleepUntil(begin + UInt64(warmup * 1e9)); stats.recording = true }.start()
        reportLoop(until: begin + UInt64((seconds + 0.5) * 1e9))
        encoder.flush()
        let summary = stats.summaryJSON(config: config, seconds: seconds - warmup)
        print("SUMMARY " + summary)
        if !jsonPath.isEmpty { try? summary.write(toFile: jsonPath, atomically: true, encoding: .utf8) }
        if let d = rx.decoder, d.errors > 0 { print("decode errors: \(d.errors)") }
        print("dropped frames: \(rx.receiver.framesDropped), unreadable captures: \(unreadable)")
        fflush(stdout)
        exit(0)
    }.start()
    app.run()
    exit(0)
}
