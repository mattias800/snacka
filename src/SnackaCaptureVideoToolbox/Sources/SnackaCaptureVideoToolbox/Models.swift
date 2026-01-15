import Foundation

// MARK: - Source Listing Models

struct AvailableSources: Codable {
    let displays: [DisplaySource]
    let windows: [WindowSource]
    let applications: [ApplicationSource]
    let cameras: [CameraSource]
}

struct DisplaySource: Codable {
    let id: String
    let name: String
    let width: Int
    let height: Int
}

struct WindowSource: Codable {
    let id: String
    let name: String
    let appName: String
    let bundleId: String?
}

struct ApplicationSource: Codable {
    let bundleId: String
    let name: String
}

struct CameraSource: Codable {
    let id: String        // AVCaptureDevice.uniqueID for stable identification
    let name: String      // Localized device name
    let index: Int        // Index in device list (for fallback selection)
    let position: String  // "front", "back", or "unspecified"
}

// MARK: - Capture Configuration

enum CaptureSourceType {
    case display(index: Int)
    case window(id: Int)
    case application(bundleId: String)
    case camera(id: String)  // Camera unique ID or index as string
}

struct CaptureConfig {
    let source: CaptureSourceType
    let width: Int
    let height: Int
    let fps: Int
    let captureAudio: Bool
    let excludeCurrentProcessAudio: Bool
    let excludeAppBundleId: String?  // Bundle ID of app to exclude from audio capture
    let encodeH264: Bool             // If true, output H.264 NAL units instead of raw NV12
    let bitrateMbps: Int             // Encoding bitrate in Mbps (only used when encodeH264=true)

    init(source: CaptureSourceType, width: Int, height: Int, fps: Int, captureAudio: Bool,
         excludeCurrentProcessAudio: Bool, excludeAppBundleId: String?,
         encodeH264: Bool = false, bitrateMbps: Int = 6) {
        self.source = source
        self.width = width
        self.height = height
        self.fps = fps
        self.captureAudio = captureAudio
        self.excludeCurrentProcessAudio = excludeCurrentProcessAudio
        self.excludeAppBundleId = excludeAppBundleId
        self.encodeH264 = encodeH264
        self.bitrateMbps = bitrateMbps
    }
}

// MARK: - Output Protocol

/// Protocol marker for video/audio output streams
/// Video: BGR24 raw frames to stdout
/// Audio: PCM to stderr (interleaved with log messages using a header with format info)
/// All multi-byte fields use big-endian (network byte order) for consistency
struct AudioPacketHeader {
    static let magic: UInt32 = 0x4D434150  // "MCAP" in ASCII (big-endian)
    static let version: UInt8 = 2  // Version 2 includes format info

    let sampleCount: UInt32
    let timestamp: UInt64
    let sampleRate: UInt32      // e.g., 48000, 96000
    let bitsPerSample: UInt8    // e.g., 16, 32
    let channels: UInt8         // e.g., 1, 2
    let isFloat: UInt8          // 0 = integer PCM, 1 = float PCM

    var data: Data {
        var header = Data()
        var magic = Self.magic.bigEndian  // Convert to big-endian
        var version = Self.version
        var samples = sampleCount
        var ts = timestamp
        var rate = sampleRate
        var bits = bitsPerSample
        var ch = channels
        var floatFlag = isFloat

        header.append(Data(bytes: &magic, count: 4))      // 0-3: magic (big-endian)
        header.append(Data(bytes: &version, count: 1))    // 4: version
        header.append(Data(bytes: &bits, count: 1))       // 5: bits per sample
        header.append(Data(bytes: &ch, count: 1))         // 6: channels
        header.append(Data(bytes: &floatFlag, count: 1))  // 7: isFloat
        header.append(Data(bytes: &samples, count: 4))    // 8-11: sample count
        header.append(Data(bytes: &rate, count: 4))       // 12-15: sample rate
        header.append(Data(bytes: &ts, count: 8))         // 16-23: timestamp
        return header
    }

    static let size = 24  // Updated size: 4+1+1+1+1+4+4+8 = 24 bytes
}

// MARK: - Preview Frame Packet (for local preview)

/// Preview frame format values
enum PreviewFormat: UInt8 {
    case nv12 = 0   // NV12 (width * height * 1.5 bytes)
    case rgb24 = 1  // RGB24 (width * height * 3 bytes)
    case rgba32 = 2 // RGBA32 (width * height * 4 bytes)
}

/// Preview frame packet header for stderr unified protocol
/// Format: [magic: 4] [length: 4] [width: 2] [height: 2] [format: 1] [timestamp: 8] [pixels...]
struct PreviewPacketHeader {
    static let magic: UInt32 = 0x56455250  // "PREV" in ASCII (little-endian: 0x50524556)
    static let magicBigEndian: UInt32 = 0x50524556  // "PREV" big-endian for network byte order

    let width: UInt16
    let height: UInt16
    let format: PreviewFormat
    let timestamp: UInt64
    let pixelData: Data

    var packetData: Data {
        var packet = Data()

        // Magic (big-endian)
        var magic = Self.magicBigEndian.bigEndian
        packet.append(Data(bytes: &magic, count: 4))

        // Payload length (big-endian): header fields + pixel data
        let payloadLength = UInt32(2 + 2 + 1 + 8 + pixelData.count)
        var length = payloadLength.bigEndian
        packet.append(Data(bytes: &length, count: 4))

        // Width (big-endian)
        var w = width.bigEndian
        packet.append(Data(bytes: &w, count: 2))

        // Height (big-endian)
        var h = height.bigEndian
        packet.append(Data(bytes: &h, count: 2))

        // Format
        var fmt = format.rawValue
        packet.append(Data(bytes: &fmt, count: 1))

        // Timestamp (big-endian)
        var ts = timestamp.bigEndian
        packet.append(Data(bytes: &ts, count: 8))

        // Pixel data
        packet.append(pixelData)

        return packet
    }

    static let headerSize = 8 + 2 + 2 + 1 + 8  // magic + length + width + height + format + timestamp = 21 bytes before pixels
}

// MARK: - Log Message Packet

/// Log level values
enum LogLevel: UInt8 {
    case debug = 0
    case info = 1
    case warning = 2
    case error = 3
}

/// Log message packet header for stderr unified protocol
/// Format: [magic: 4] [length: 4] [level: 1] [message: UTF-8...]
struct LogPacketHeader {
    static let magic: UInt32 = 0x4D474F4C  // "LOGM" in ASCII (little-endian)
    static let magicBigEndian: UInt32 = 0x4C4F474D  // "LOGM" big-endian for network byte order

    let level: LogLevel
    let message: String

    var packetData: Data {
        var packet = Data()
        let messageData = message.data(using: .utf8) ?? Data()

        // Magic (big-endian)
        var magic = Self.magicBigEndian.bigEndian
        packet.append(Data(bytes: &magic, count: 4))

        // Payload length (big-endian): level + message
        let payloadLength = UInt32(1 + messageData.count)
        var length = payloadLength.bigEndian
        packet.append(Data(bytes: &length, count: 4))

        // Level
        var lvl = level.rawValue
        packet.append(Data(bytes: &lvl, count: 1))

        // Message
        packet.append(messageData)

        return packet
    }
}

// MARK: - Stderr Output Helper

/// Helper for writing to stderr with unified packet protocol
class StderrWriter {
    static let shared = StderrWriter()
    private let stderr = FileHandle.standardError
    private let lock = NSLock()

    /// Write a preview frame to stderr
    func writePreviewFrame(width: Int, height: Int, format: PreviewFormat, timestamp: UInt64, pixelData: Data) {
        let header = PreviewPacketHeader(
            width: UInt16(width),
            height: UInt16(height),
            format: format,
            timestamp: timestamp,
            pixelData: pixelData
        )

        lock.lock()
        defer { lock.unlock() }

        try? stderr.write(contentsOf: header.packetData)
    }

    /// Write a log message to stderr (plain text for backward compatibility)
    func log(_ message: String) {
        lock.lock()
        defer { lock.unlock() }

        if let data = "\(message)\n".data(using: .utf8) {
            try? stderr.write(contentsOf: data)
        }
    }

    /// Write audio packet to stderr
    func writeAudioPacket(header: AudioPacketHeader, audioData: Data) {
        lock.lock()
        defer { lock.unlock() }

        try? stderr.write(contentsOf: header.data)
        try? stderr.write(contentsOf: audioData)
    }
}
