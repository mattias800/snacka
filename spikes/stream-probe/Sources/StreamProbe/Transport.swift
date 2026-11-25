import Darwin
import Foundation

/// Minimal unreliable frame transport over UDP. No jitter buffer, no retransmission:
/// a frame is handed to the decoder the instant its last packet arrives.
///
/// Datagram = 32-byte header + payload chunk. Frame payload =
///   [u8 paramSetCount] ([u16 len][bytes])* [AVCC NAL units...]
/// Header bytes 14-15 carry a random per-session stream id, so receivers can detect a restarted sender.
enum Wire {
    static let magic: UInt32 = 0x534E_4B50 // "SNKP"
    /// Receiver -> sender: [u32 magic "SNKR"][u16 stream id]. Asks for a keyframe (join, loss, decode error).
    static let keyframeRequestMagic: UInt32 = 0x534E_4B52
    static let headerSize = 32
    static let datagramSize = 1200 // conservative for real networks (MTU/VPN/PPPoE)
    static let chunkSize = datagramSize - headerSize
}

struct ReceivedFrame {
    let frameId: UInt64
    let captureNs: UInt64
    let encodedNs: UInt64
    let receivedNs: UInt64
    let codec: Codec
    let isKeyframe: Bool
    let parameterSets: [Data]
    let avcc: Data
    let wireBytes: Int
}

private func makeAddress(host: String, port: UInt16) -> sockaddr_in {
    var addr = sockaddr_in()
    addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
    addr.sin_family = sa_family_t(AF_INET)
    addr.sin_port = port.bigEndian
    inet_pton(AF_INET, host, &addr.sin_addr)
    return addr
}

private func setBuffer(_ fd: Int32, _ option: Int32, _ bytes: Int32) {
    var size = bytes
    while size > 65536 && setsockopt(fd, SOL_SOCKET, option, &size, socklen_t(MemoryLayout<Int32>.size)) != 0 {
        size /= 2
    }
}

final class UdpSender {
    private let fd: Int32
    private var dest: sockaddr_in
    private var datagram = [UInt8](repeating: 0, count: Wire.datagramSize)
    private(set) var bytesSent = 0
    private(set) var sendRetries = 0
    let streamId = UInt16.random(in: 1...UInt16.max)
    private let keyframeLock = NSLock()
    private var keyframeWanted = false
    private(set) var keyframeRequests = 0

    init(host: String, port: UInt16) {
        fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        setBuffer(fd, SO_SNDBUF, 8 << 20)
        dest = makeAddress(host: host, port: port)
        var local = makeAddress(host: "0.0.0.0", port: 0)
        _ = withUnsafePointer(to: &local) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        let listener = Thread { [self] in
            var buf = [UInt8](repeating: 0, count: 64)
            while true {
                let n = recv(fd, &buf, buf.count, 0)
                guard n >= 6 else { continue }
                let magic = UInt32(buf[0]) | UInt32(buf[1]) << 8 | UInt32(buf[2]) << 16 | UInt32(buf[3]) << 24
                let sid = UInt16(buf[4]) | UInt16(buf[5]) << 8
                if magic == Wire.keyframeRequestMagic && sid == streamId {
                    keyframeLock.lock(); keyframeWanted = true; keyframeRequests += 1; keyframeLock.unlock()
                }
            }
        }
        listener.qualityOfService = .userInteractive
        listener.start()
    }

    /// True once per pending keyframe request.
    func takeKeyframeRequest() -> Bool {
        keyframeLock.lock(); defer { keyframeLock.unlock() }
        let wanted = keyframeWanted
        keyframeWanted = false
        return wanted
    }

    func send(_ frame: EncodedFrame, codec: Codec) {
        var payload = Data()
        payload.append(UInt8(frame.parameterSets.count))
        for p in frame.parameterSets {
            var len = UInt16(p.count).littleEndian
            withUnsafeBytes(of: &len) { payload.append(contentsOf: $0) }
            payload.append(p)
        }
        payload.append(frame.avcc)

        let count = (payload.count + Wire.chunkSize - 1) / Wire.chunkSize
        payload.withUnsafeBytes { (src: UnsafeRawBufferPointer) in
            datagram.withUnsafeMutableBytes { dst in
                dst.storeBytes(of: Wire.magic.littleEndian, toByteOffset: 0, as: UInt32.self)
                dst.storeBytes(of: UInt32(truncatingIfNeeded: frame.frameId).littleEndian, toByteOffset: 4, as: UInt32.self)
                dst.storeBytes(of: UInt16(count).littleEndian, toByteOffset: 10, as: UInt16.self)
                dst.storeBytes(of: UInt8(frame.isKeyframe ? 1 : 0), toByteOffset: 12, as: UInt8.self)
                dst.storeBytes(of: codec.wireId, toByteOffset: 13, as: UInt8.self)
                dst.storeBytes(of: streamId.littleEndian, toByteOffset: 14, as: UInt16.self)
                dst.storeBytes(of: frame.captureNs.littleEndian, toByteOffset: 16, as: UInt64.self)
                dst.storeBytes(of: frame.encodedNs.littleEndian, toByteOffset: 24, as: UInt64.self)
                for i in 0..<count {
                    let start = i * Wire.chunkSize
                    let len = min(Wire.chunkSize, payload.count - start)
                    dst.storeBytes(of: UInt16(i).littleEndian, toByteOffset: 8, as: UInt16.self)
                    memcpy(dst.baseAddress! + Wire.headerSize, src.baseAddress! + start, len)
                    let total = Wire.headerSize + len
                    withUnsafePointer(to: &dest) { addr in
                        addr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sa in
                            while sendto(fd, dst.baseAddress!, total, 0, sa, socklen_t(MemoryLayout<sockaddr_in>.size)) < 0 {
                                // ENOBUFS on large keyframe bursts: back off briefly and retry.
                                sendRetries += 1
                                usleep(50)
                            }
                        }
                    }
                    bytesSent += total
                }
            }
        }
    }
}

final class UdpReceiver {
    private let fd: Int32
    private var partial: [UInt32: (count: Int, got: Int, chunks: [Data?], key: Bool, codec: UInt8, cap: UInt64, enc: UInt64, bytes: Int)] = [:]
    private var lastDelivered: Int64 = -1
    private(set) var framesDropped = 0
    var onFrame: ((ReceivedFrame) -> Void)?

    init(port: UInt16) {
        fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        setBuffer(fd, SO_RCVBUF, 8 << 20)
        var addr = makeAddress(host: "0.0.0.0", port: port)
        var reuse: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &reuse, socklen_t(MemoryLayout<Int32>.size))
        let rc = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        precondition(rc == 0, "bind failed: \(errno)")
    }

    func run() {
        let thread = Thread { [self] in
            var buf = [UInt8](repeating: 0, count: 65536)
            while true {
                let n = recv(fd, &buf, buf.count, 0)
                if n < Wire.headerSize { continue }
                handle(buf, n)
            }
        }
        thread.qualityOfService = .userInteractive
        thread.start()
    }

    private func handle(_ buf: [UInt8], _ n: Int) {
        buf.withUnsafeBytes { raw in
            guard UInt32(littleEndian: raw.load(fromByteOffset: 0, as: UInt32.self)) == Wire.magic else { return }
            let fid = UInt32(littleEndian: raw.load(fromByteOffset: 4, as: UInt32.self))
            if Int64(fid) <= lastDelivered { return }
            let idx = Int(UInt16(littleEndian: raw.load(fromByteOffset: 8, as: UInt16.self)))
            let count = Int(UInt16(littleEndian: raw.load(fromByteOffset: 10, as: UInt16.self)))
            var entry = partial[fid] ?? (count, 0, Array(repeating: nil, count: count),
                raw.load(fromByteOffset: 12, as: UInt8.self) == 1, raw.load(fromByteOffset: 13, as: UInt8.self),
                UInt64(littleEndian: raw.loadUnaligned(fromByteOffset: 16, as: UInt64.self)),
                UInt64(littleEndian: raw.loadUnaligned(fromByteOffset: 24, as: UInt64.self)), 0)
            if idx < entry.count, entry.chunks[idx] == nil {
                entry.chunks[idx] = Data(raw[Wire.headerSize..<n])
                entry.got += 1
                entry.bytes += n
            }
            if entry.got == entry.count {
                partial.removeValue(forKey: fid)
                // Anything older that is still incomplete will never be decodable in order: drop it.
                for old in partial.keys where old < fid { partial.removeValue(forKey: old); framesDropped += 1 }
                lastDelivered = Int64(fid)
                deliver(fid, entry)
            } else {
                partial[fid] = entry
            }
        }
    }

    private func deliver(_ fid: UInt32, _ e: (count: Int, got: Int, chunks: [Data?], key: Bool, codec: UInt8, cap: UInt64, enc: UInt64, bytes: Int)) {
        let receivedNs = nowNs()
        var payload = Data(capacity: e.count * Wire.chunkSize)
        for c in e.chunks { payload.append(c!) }
        var params: [Data] = []
        var pos = 1
        let paramCount = Int(payload[payload.startIndex])
        for _ in 0..<paramCount {
            let len = Int(payload[payload.startIndex + pos]) | Int(payload[payload.startIndex + pos + 1]) << 8
            pos += 2
            params.append(payload.subdata(in: (payload.startIndex + pos)..<(payload.startIndex + pos + len)))
            pos += len
        }
        let avcc = payload.subdata(in: (payload.startIndex + pos)..<payload.endIndex)
        onFrame?(ReceivedFrame(frameId: UInt64(fid), captureNs: e.cap, encodedNs: e.enc, receivedNs: receivedNs,
                               codec: Codec(wireId: e.codec) ?? .h264, isKeyframe: e.key, parameterSets: params,
                               avcc: avcc, wireBytes: e.bytes))
    }
}
