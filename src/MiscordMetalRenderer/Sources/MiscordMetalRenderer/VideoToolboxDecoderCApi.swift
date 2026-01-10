import Foundation

// MARK: - C API for VideoToolbox Decoder

private var decoderInstances: [UnsafeMutableRawPointer: VideoToolboxDecoder] = [:]
private let decoderLock = NSLock()

@_cdecl("vt_decoder_create")
public func vt_decoder_create() -> UnsafeMutableRawPointer? {
    guard let decoder = VideoToolboxDecoder() else {
        return nil
    }

    let pointer = Unmanaged.passRetained(decoder).toOpaque()
    decoderLock.lock()
    decoderInstances[pointer] = decoder
    decoderLock.unlock()

    return pointer
}

@_cdecl("vt_decoder_destroy")
public func vt_decoder_destroy(_ decoder: UnsafeMutableRawPointer?) {
    guard let pointer = decoder else { return }

    decoderLock.lock()
    if let decoder = decoderInstances.removeValue(forKey: pointer) {
        Unmanaged.passUnretained(decoder).release()
    }
    decoderLock.unlock()
}

@_cdecl("vt_decoder_initialize")
public func vt_decoder_initialize(
    _ decoder: UnsafeMutableRawPointer?,
    _ width: Int32,
    _ height: Int32,
    _ spsData: UnsafePointer<UInt8>?,
    _ spsLength: Int32,
    _ ppsData: UnsafePointer<UInt8>?,
    _ ppsLength: Int32
) -> Bool {
    guard let pointer = decoder,
          let spsPtr = spsData,
          let ppsPtr = ppsData else {
        return false
    }

    decoderLock.lock()
    let decoderInstance = decoderInstances[pointer]
    decoderLock.unlock()

    guard let instance = decoderInstance else { return false }

    let sps = Data(bytes: spsPtr, count: Int(spsLength))
    let pps = Data(bytes: ppsPtr, count: Int(ppsLength))

    return instance.initialize(width: Int(width), height: Int(height), sps: sps, pps: pps)
}

@_cdecl("vt_decoder_decode_and_render")
public func vt_decoder_decode_and_render(
    _ decoder: UnsafeMutableRawPointer?,
    _ nalData: UnsafePointer<UInt8>?,
    _ nalLength: Int32,
    _ isKeyframe: Bool
) -> Bool {
    guard let pointer = decoder,
          let nalPtr = nalData else {
        return false
    }

    decoderLock.lock()
    let decoderInstance = decoderInstances[pointer]
    decoderLock.unlock()

    guard let instance = decoderInstance else { return false }

    let nal = Data(bytes: nalPtr, count: Int(nalLength))
    return instance.decodeAndRender(nalUnit: nal, isKeyframe: isKeyframe)
}

@_cdecl("vt_decoder_get_view")
public func vt_decoder_get_view(_ decoder: UnsafeMutableRawPointer?) -> UnsafeMutableRawPointer? {
    guard let pointer = decoder else { return nil }

    decoderLock.lock()
    let decoderInstance = decoderInstances[pointer]
    decoderLock.unlock()

    guard let instance = decoderInstance else { return nil }

    return Unmanaged.passUnretained(instance.view).toOpaque()
}

@_cdecl("vt_decoder_set_display_size")
public func vt_decoder_set_display_size(
    _ decoder: UnsafeMutableRawPointer?,
    _ width: Int32,
    _ height: Int32
) {
    guard let pointer = decoder else { return }

    decoderLock.lock()
    let decoderInstance = decoderInstances[pointer]
    decoderLock.unlock()

    decoderInstance?.setDisplaySize(width: Int(width), height: Int(height))
}

@_cdecl("vt_decoder_detach_view")
public func vt_decoder_detach_view(_ decoder: UnsafeMutableRawPointer?) {
    guard let pointer = decoder else { return }

    decoderLock.lock()
    let decoderInstance = decoderInstances[pointer]
    decoderLock.unlock()

    decoderInstance?.detachView()
}

@_cdecl("vt_decoder_is_available")
public func vt_decoder_is_available() -> Bool {
    // VideoToolbox is always available on macOS 10.8+
    return true
}
