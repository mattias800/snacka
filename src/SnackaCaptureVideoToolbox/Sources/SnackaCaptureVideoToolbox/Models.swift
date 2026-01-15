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
struct AudioPacketHeader {
    static let magic: UInt32 = 0x4D434150  // "MCAP" in ASCII
    static let version: UInt8 = 2  // Version 2 includes format info

    let sampleCount: UInt32
    let timestamp: UInt64
    let sampleRate: UInt32      // e.g., 48000, 96000
    let bitsPerSample: UInt8    // e.g., 16, 32
    let channels: UInt8         // e.g., 1, 2
    let isFloat: UInt8          // 0 = integer PCM, 1 = float PCM

    var data: Data {
        var header = Data()
        var magic = Self.magic
        var version = Self.version
        var samples = sampleCount
        var ts = timestamp
        var rate = sampleRate
        var bits = bitsPerSample
        var ch = channels
        var floatFlag = isFloat

        header.append(Data(bytes: &magic, count: 4))      // 0-3: magic
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
