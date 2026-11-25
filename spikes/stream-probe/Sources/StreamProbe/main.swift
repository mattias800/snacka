import CoreVideo
import Foundation

// stream-probe: latency floor of synthetic frame -> HW encode -> UDP -> HW decode -> barcode read.
//
//   stream-probe loop --width 3840 --height 2160 --fps 120 --codec hevc --bitrate 80 --seconds 10
//   stream-probe recv --port 9000            (on the viewer)
//   stream-probe send --host 1.2.3.4 --port 9000 --width 1920 --height 1080 --fps 60
//
// "total" is measured from the timestamp embedded in the pixels, so it is only meaningful when
// sender and receiver share a clock (same machine). Display/present latency is NOT included.

var args = Array(CommandLine.arguments.dropFirst())
let mode = args.isEmpty ? "loop" : args.removeFirst()
func opt(_ name: String, _ fallback: String) -> String {
    if let i = args.firstIndex(of: "--\(name)"), i + 1 < args.count { return args[i + 1] }
    return fallback
}
func flag(_ name: String) -> Bool { args.contains("--\(name)") }

let width = Int(opt("width", "1920"))!
let height = Int(opt("height", "1080"))!
let fps = Int(opt("fps", "60"))!
let codec = Codec(rawValue: opt("codec", "h264"))!
let bitrate = Double(opt("bitrate", "20"))!
let seconds = Double(opt("seconds", "10"))!
let host = opt("host", "127.0.0.1")
let port = UInt16(opt("port", "9000"))!
let lowLatency = !flag("no-lowlatency")
let jsonPath = opt("json", "")
let warmup = Double(opt("warmup", "1"))!
let realTime = opt("realtime", "on") == "on"
let quality = Double(opt("quality", ""))

let stats = Stats()
var config: [String: Any] = ["mode": mode, "width": width, "height": height, "fps": fps, "codec": codec.rawValue,
                             "bitrateMbps": bitrate, "lowLatencyRC": lowLatency,
                             "realTime": realTime, "quality": quality ?? -1]

// MARK: receiver side

final class ReceiverPipeline {
    let receiver: UdpReceiver
    var decoder: Decoder?
    private var headers: [UInt64: ReceivedFrame] = [:]
    private let lock = NSLock()

    init(port: UInt16) {
        receiver = UdpReceiver(port: port)
        receiver.onFrame = { [unowned self] f in self.onReceived(f) }
    }

    private func onReceived(_ f: ReceivedFrame) {
        if decoder == nil {
            decoder = Decoder(codec: f.codec)
            decoder!.onFrame = { [unowned self] buffer, doneNs in self.onDecoded(buffer, doneNs) }
        }
        lock.lock(); headers[f.frameId] = f; lock.unlock()
        stats.count("wireBytes", f.wireBytes)
        decoder!.decode(isKeyframe: f.isKeyframe, parameterSets: f.parameterSets, avcc: f.avcc)
    }

    private func onDecoded(_ buffer: CVPixelBuffer, _ doneNs: UInt64) {
        CVPixelBufferLockBaseAddress(buffer, .readOnly)
        let code = Barcode.read(buffer)
        CVPixelBufferUnlockBaseAddress(buffer, .readOnly)
        guard let code else { stats.count("badBarcode"); return }
        lock.lock()
        let header = headers.removeValue(forKey: code.frameId & 0xFFFF_FFFF)
        for k in headers.keys where k < (code.frameId & 0xFFFF_FFFF) { headers.removeValue(forKey: k) }
        lock.unlock()
        guard let h = header, h.captureNs == code.captureNs else { stats.count("badBarcode"); return }
        stats.add("encode", h.encodedNs - h.captureNs)
        stats.add("network", h.receivedNs - h.encodedNs)
        stats.add("decode", doneNs - h.receivedNs)
        stats.add("total", doneNs - code.captureNs)
    }

    func start() { receiver.run() }
}

func reportLoop(until endNs: UInt64?) {
    var last = nowNs()
    while endNs == nil || nowNs() < endNs! {
        sleepUntil(last + 1_000_000_000)
        let now = nowNs()
        print(stats.line(seconds: Double(now - last) / 1e9))
        fflush(stdout)
        last = now
    }
}

// MARK: sender side

final class Counter {
    private let lock = NSLock()
    private var value = 0
    func increment() { lock.lock(); value += 1; lock.unlock() }
    func load() -> Int { lock.lock(); defer { lock.unlock() }; return value }
}
let sourceFrames = Counter()

func runSender(host: String, port: UInt16) throws -> Thread {
    let source = FrameSource(width: width, height: height)
    let encoder = try Encoder(codec: codec, width: width, height: height, fps: fps, bitrateMbps: bitrate,
                              lowLatencyRC: lowLatency, realTime: realTime, quality: quality)
    config["lowLatencyRC"] = encoder.lowLatencyRC
    let sender = UdpSender(host: host, port: port)
    encoder.onFrame = { frame in
        sender.send(frame, codec: codec)
        stats.count("sentFrames")
        stats.add("encodeOnly", frame.encodedNs - frame.captureNs)
    }
    let thread = Thread {
        let period = UInt64(1_000_000_000 / fps)
        let start = nowNs() + 50_000_000
        var frameId: UInt64 = 0
        var late = 0
        let total = UInt64(seconds * Double(fps))
        while frameId < total {
            let deadline = start + frameId * period
            if nowNs() > deadline + period { late += 1 } else { sleepUntil(deadline) }
            let (buffer, captureNs) = source.next(frameId: frameId)
            sourceFrames.increment()
            let forceKey = frameId == 0 || sender.takeKeyframeRequest()
            encoder.encode(buffer, frameId: frameId, captureNs: captureNs, forceKeyframe: forceKey)
            frameId += 1
        }
        encoder.flush()
        if late > 0 { fputs("sender: \(late) frames generated late (source could not keep up)\n", stderr) }
        if sender.sendRetries > 0 { fputs("sender: \(sender.sendRetries) send retries (socket buffer full)\n", stderr) }
        fputs("sender: stream id \(sender.streamId), \(sender.keyframeRequests) keyframe requests served\n", stderr)
    }
    thread.qualityOfService = .userInteractive
    return thread
}

// MARK: modes

print("stream-probe \(mode): \(width)x\(height)@\(fps) \(codec.rawValue) \(bitrate) Mbps lowLatencyRC=\(lowLatency)")
switch mode {
case "loop":
    let rx = ReceiverPipeline(port: port)
    rx.start()
    let tx = try runSender(host: "127.0.0.1", port: port)
    let begin = nowNs()
    tx.start()
    // Warm-up (session creation, first keyframe) is excluded from the summary.
    Thread { sleepUntil(begin + UInt64(warmup * 1e9)); stats.recording = true }.start()
    reportLoop(until: begin + UInt64((seconds + 0.5) * 1e9))
    let summary = stats.summaryJSON(config: config, seconds: seconds - warmup)
    print("SUMMARY " + summary)
    if !jsonPath.isEmpty { try summary.write(toFile: jsonPath, atomically: true, encoding: .utf8) }
    if let d = rx.decoder, d.errors > 0 { print("decode errors: \(d.errors)") }
    print("dropped frames: \(rx.receiver.framesDropped)")
case "recv":
    stats.recording = true
    let rx = ReceiverPipeline(port: port)
    rx.start()
    reportLoop(until: nil)
case "send":
    let tx = try runSender(host: host, port: port)
    tx.start()
    // Per-second sender report: frames that came out of the encoder and were sent.
    let begin = nowNs()
    var last = begin
    var generatedBefore = 0
    while nowNs() < begin + UInt64((seconds + 1) * 1e9) {
        sleepUntil(last + 1_000_000_000)
        let now = nowNs()
        let line = stats.line(seconds: Double(now - last) / 1e9)
        let generated = sourceFrames.load()
        print("sender: generated \(generated - generatedBefore)/s | \(line)")
        fflush(stdout)
        generatedBefore = generated
        last = now
    }
default:
    fputs("usage: stream-probe [loop|send|recv] [--width W --height H --fps N --codec h264|hevc --bitrate Mbps --seconds S --warmup S --realtime on|off --quality 0..1 --host H --port P --no-lowlatency --json path]\n", stderr)
    exit(2)
}
