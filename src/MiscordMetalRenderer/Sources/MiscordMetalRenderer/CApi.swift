import Foundation
import AppKit

/// Opaque handle to a MetalVideoRenderer instance.
public typealias MetalRendererHandle = UnsafeMutableRawPointer

/// Dictionary to store renderer instances by handle.
private var renderers: [UnsafeMutableRawPointer: MetalVideoRenderer] = [:]
private let lock = NSLock()

// MARK: - C API for P/Invoke

/// Creates a new Metal video renderer.
/// Returns a handle to the renderer, or nil if creation failed.
@_cdecl("metal_renderer_create")
public func metalRendererCreate() -> MetalRendererHandle? {
    guard let renderer = MetalVideoRenderer() else {
        return nil
    }

    let handle = Unmanaged.passRetained(renderer).toOpaque()
    lock.lock()
    renderers[handle] = renderer
    lock.unlock()

    return handle
}

/// Destroys a Metal video renderer.
@_cdecl("metal_renderer_destroy")
public func metalRendererDestroy(_ handle: MetalRendererHandle?) {
    guard let handle = handle else { return }

    lock.lock()
    renderers.removeValue(forKey: handle)
    lock.unlock()

    Unmanaged<MetalVideoRenderer>.fromOpaque(handle).release()
}

/// Gets the NSView pointer for the renderer.
/// This pointer can be used with Avalonia's NativeControlHost.
@_cdecl("metal_renderer_get_view")
public func metalRendererGetView(_ handle: MetalRendererHandle?) -> UnsafeMutableRawPointer? {
    guard let handle = handle else { return nil }

    lock.lock()
    let renderer = renderers[handle]
    lock.unlock()

    guard let renderer = renderer else { return nil }

    return Unmanaged.passUnretained(renderer.view).toOpaque()
}

/// Initializes the renderer with video dimensions.
/// Returns true if successful.
@_cdecl("metal_renderer_initialize")
public func metalRendererInitialize(_ handle: MetalRendererHandle?, _ width: Int32, _ height: Int32) -> Bool {
    guard let handle = handle else { return false }

    lock.lock()
    let renderer = renderers[handle]
    lock.unlock()

    guard let renderer = renderer else { return false }

    return renderer.initialize(width: Int(width), height: Int(height))
}

/// Renders a frame from NV12 data.
/// nv12Data: Pointer to NV12 frame data (Y plane followed by UV plane)
/// length: Total length of NV12 data in bytes
@_cdecl("metal_renderer_render_frame")
public func metalRendererRenderFrame(_ handle: MetalRendererHandle?, _ nv12Data: UnsafePointer<UInt8>?, _ length: Int32) {
    guard let handle = handle, let nv12Data = nv12Data else { return }

    lock.lock()
    let renderer = renderers[handle]
    lock.unlock()

    renderer?.renderFrame(nv12Data, length: Int(length))
}

/// Sets the display size for the renderer.
@_cdecl("metal_renderer_set_display_size")
public func metalRendererSetDisplaySize(_ handle: MetalRendererHandle?, _ width: Int32, _ height: Int32) {
    guard let handle = handle else { return }

    lock.lock()
    let renderer = renderers[handle]
    lock.unlock()

    renderer?.setDisplaySize(width: Int(width), height: Int(height))
}

/// Checks if Metal rendering is available on this system.
@_cdecl("metal_renderer_is_available")
public func metalRendererIsAvailable() -> Bool {
    return MTLCreateSystemDefaultDevice() != nil
}

/// Gets the video width.
@_cdecl("metal_renderer_get_width")
public func metalRendererGetWidth(_ handle: MetalRendererHandle?) -> Int32 {
    guard let handle = handle else { return 0 }

    lock.lock()
    let renderer = renderers[handle]
    lock.unlock()

    // We'd need to expose this from MetalVideoRenderer
    // For now return 0 as placeholder
    return 0
}

/// Gets the video height.
@_cdecl("metal_renderer_get_height")
public func metalRendererGetHeight(_ handle: MetalRendererHandle?) -> Int32 {
    guard let handle = handle else { return 0 }

    lock.lock()
    let renderer = renderers[handle]
    lock.unlock()

    return 0
}
