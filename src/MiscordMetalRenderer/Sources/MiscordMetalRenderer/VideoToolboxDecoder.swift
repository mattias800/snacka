import Foundation
import VideoToolbox
import CoreVideo
import Metal
import MetalKit
import AppKit

/// Hardware H264 decoder using VideoToolbox with direct Metal rendering.
/// Zero-copy pipeline: H264 NAL → VideoToolbox → CVPixelBuffer → Metal Texture → Display
public class VideoToolboxDecoder {
    private var decompressionSession: VTDecompressionSession?
    private var formatDescription: CMVideoFormatDescription?

    private let device: MTLDevice
    private let metalView: MetalVideoView
    private var videoWidth: Int = 0
    private var videoHeight: Int = 0

    private var spsData: Data?
    private var ppsData: Data?

    // Synchronization
    private let decodeLock = NSLock()
    var lastPixelBuffer: CVPixelBuffer?  // Internal for callback access

    public init?() {
        guard let device = MTLCreateSystemDefaultDevice() else {
            return nil
        }
        self.device = device

        // Create Metal view (it handles its own command queue and rendering)
        self.metalView = MetalVideoView(device: device)
    }

    deinit {
        destroyDecompressionSession()
    }

    public var view: NSView {
        return metalView
    }

    public func initialize(width: Int, height: Int, sps: Data, pps: Data) -> Bool {
        decodeLock.lock()
        defer { decodeLock.unlock() }

        self.videoWidth = width
        self.videoHeight = height
        self.spsData = sps
        self.ppsData = pps

        // Create format description from SPS/PPS
        guard let formatDesc = createFormatDescription(sps: sps, pps: pps) else {
            return false
        }
        self.formatDescription = formatDesc

        // Create decompression session
        if !createDecompressionSession(formatDescription: formatDesc) {
            return false
        }

        // Update Metal view size
        metalView.setFrameSize(NSSize(width: width, height: height))

        return true
    }

    public func decodeAndRender(nalUnit: Data, isKeyframe: Bool) -> Bool {
        decodeLock.lock()
        defer { decodeLock.unlock() }

        guard let session = decompressionSession, let formatDesc = formatDescription else {
            return false
        }

        // Create sample buffer from NAL unit
        guard let sampleBuffer = createSampleBuffer(nalUnit: nalUnit, formatDescription: formatDesc) else {
            return false
        }

        // Decode
        var flagOut: VTDecodeInfoFlags = []
        let status = VTDecompressionSessionDecodeFrame(
            session,
            sampleBuffer: sampleBuffer,
            flags: [._EnableAsynchronousDecompression],
            frameRefcon: nil,
            infoFlagsOut: &flagOut
        )

        if status != noErr {
            return false
        }

        // Wait for decode to complete and render
        VTDecompressionSessionWaitForAsynchronousFrames(session)

        // Render the last decoded frame
        if let pixelBuffer = lastPixelBuffer {
            renderPixelBuffer(pixelBuffer)
            lastPixelBuffer = nil  // Clear after rendering to detect new frames
            return true
        }

        return false
    }

    public func setDisplaySize(width: Int, height: Int) {
        DispatchQueue.main.async {
            self.metalView.setFrameSize(NSSize(width: width, height: height))
        }
    }

    /// Detaches the Metal view from its current superview.
    /// This must be called before re-embedding the view in a different parent.
    public func detachView() {
        DispatchQueue.main.async {
            if self.metalView.superview != nil {
                self.metalView.removeFromSuperview()
            }
        }
    }

    // MARK: - Private Methods

    private func createFormatDescription(sps: Data, pps: Data) -> CMVideoFormatDescription? {
        var formatDescription: CMVideoFormatDescription?

        // Convert Data to Array to ensure stable memory
        let spsArray = [UInt8](sps)
        let ppsArray = [UInt8](pps)

        let status = spsArray.withUnsafeBufferPointer { spsBuffer in
            ppsArray.withUnsafeBufferPointer { ppsBuffer in
                // Create array of pointers inside the closure where they're valid
                let parameterSetPointers: [UnsafePointer<UInt8>] = [
                    spsBuffer.baseAddress!,
                    ppsBuffer.baseAddress!
                ]
                let parameterSetSizes: [Int] = [spsArray.count, ppsArray.count]

                return parameterSetPointers.withUnsafeBufferPointer { pointersBuffer in
                    parameterSetSizes.withUnsafeBufferPointer { sizesBuffer in
                        CMVideoFormatDescriptionCreateFromH264ParameterSets(
                            allocator: nil,
                            parameterSetCount: 2,
                            parameterSetPointers: pointersBuffer.baseAddress!,
                            parameterSetSizes: sizesBuffer.baseAddress!,
                            nalUnitHeaderLength: 4,
                            formatDescriptionOut: &formatDescription
                        )
                    }
                }
            }
        }

        if status != noErr {
            return nil
        }

        return formatDescription
    }

    private func createDecompressionSession(formatDescription: CMVideoFormatDescription) -> Bool {
        destroyDecompressionSession()

        // Configure for NV12 output (bi-planar YUV, optimal for Metal)
        let destinationAttributes: [String: Any] = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
            kCVPixelBufferMetalCompatibilityKey as String: true,
            kCVPixelBufferWidthKey as String: videoWidth,
            kCVPixelBufferHeightKey as String: videoHeight
        ]

        // Callback for decoded frames
        var callback = VTDecompressionOutputCallbackRecord(
            decompressionOutputCallback: decompressionCallback,
            decompressionOutputRefCon: Unmanaged.passUnretained(self).toOpaque()
        )

        var session: VTDecompressionSession?
        let status = VTDecompressionSessionCreate(
            allocator: nil,
            formatDescription: formatDescription,
            decoderSpecification: nil,
            imageBufferAttributes: destinationAttributes as CFDictionary,
            outputCallback: &callback,
            decompressionSessionOut: &session
        )

        if status != noErr {
            return false
        }

        self.decompressionSession = session

        // Set real-time mode for lowest latency
        if let session = session {
            VTSessionSetProperty(session, key: kVTDecompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        }

        return true
    }

    private func destroyDecompressionSession() {
        if let session = decompressionSession {
            VTDecompressionSessionInvalidate(session)
            decompressionSession = nil
        }
    }

    private func createSampleBuffer(nalUnit: Data, formatDescription: CMVideoFormatDescription) -> CMSampleBuffer? {
        // Convert NAL unit to AVCC format (4-byte length prefix)
        let totalLength = nalUnit.count + 4
        var avccData = Data(capacity: totalLength)
        var length = UInt32(nalUnit.count).bigEndian
        avccData.append(Data(bytes: &length, count: 4))
        avccData.append(nalUnit)

        var blockBuffer: CMBlockBuffer?

        // First create an empty block buffer, then copy data into it
        // This ensures proper memory management (the block buffer owns the memory)
        var createStatus = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault,
            memoryBlock: nil,  // Let CMBlockBuffer allocate memory
            blockLength: totalLength,
            blockAllocator: kCFAllocatorDefault,
            customBlockSource: nil,
            offsetToData: 0,
            dataLength: totalLength,
            flags: kCMBlockBufferAssureMemoryNowFlag,
            blockBufferOut: &blockBuffer
        )

        guard createStatus == kCMBlockBufferNoErr, let buffer = blockBuffer else {
            return nil
        }

        // Copy data into the block buffer
        let copyStatus = avccData.withUnsafeBytes { ptr in
            CMBlockBufferReplaceDataBytes(
                with: ptr.baseAddress!,
                blockBuffer: buffer,
                offsetIntoDestination: 0,
                dataLength: totalLength
            )
        }

        guard copyStatus == kCMBlockBufferNoErr else {
            return nil
        }

        var sampleBuffer: CMSampleBuffer?
        var sampleSize = totalLength

        let sampleStatus = CMSampleBufferCreate(
            allocator: nil,
            dataBuffer: buffer,
            dataReady: true,
            makeDataReadyCallback: nil,
            refcon: nil,
            formatDescription: formatDescription,
            sampleCount: 1,
            sampleTimingEntryCount: 0,
            sampleTimingArray: nil,
            sampleSizeEntryCount: 1,
            sampleSizeArray: &sampleSize,
            sampleBufferOut: &sampleBuffer
        )

        guard sampleStatus == noErr else {
            return nil
        }

        return sampleBuffer
    }

    private func renderPixelBuffer(_ pixelBuffer: CVPixelBuffer) {
        // Pass the pixel buffer to the view - it will be rendered on the next frame
        metalView.setPixelBuffer(pixelBuffer)
    }
}

// VideoToolbox decode callback
private func decompressionCallback(
    decompressionOutputRefCon: UnsafeMutableRawPointer?,
    sourceFrameRefCon: UnsafeMutableRawPointer?,
    status: OSStatus,
    infoFlags: VTDecodeInfoFlags,
    imageBuffer: CVImageBuffer?,
    presentationTimeStamp: CMTime,
    presentationDuration: CMTime
) {
    guard let refCon = decompressionOutputRefCon else {
        return
    }
    let decoder = Unmanaged<VideoToolboxDecoder>.fromOpaque(refCon).takeUnretainedValue()

    if status == noErr, let pixelBuffer = imageBuffer {
        decoder.lastPixelBuffer = pixelBuffer as CVPixelBuffer
    }
}

/// Metal view for displaying decoded video using MTKView with delegate
/// Uses a child window overlay to bypass Avalonia's compositor issues
class MetalVideoView: MTKView, MTKViewDelegate {
    private var targetSize: NSSize = .zero
    private var commandQueue: MTLCommandQueue?
    private var pendingPixelBuffer: CVPixelBuffer?
    private var textureCache: CVMetalTextureCache?
    private var pipelineState: MTLRenderPipelineState?

    // Video dimensions for aspect ratio calculation
    private var videoWidth: Int = 0
    private var videoHeight: Int = 0

    // Child window for overlay rendering
    private var overlayWindow: NSWindow?
    private var overlayView: MTKView?

    // Use flipped coordinates to match Avalonia's coordinate system
    override var isFlipped: Bool {
        return true
    }

    init(device: MTLDevice) {
        // Store device for later use
        self.storedDevice = device

        super.init(frame: .zero, device: device)

        // Configure this view as a placeholder (transparent)
        self.colorPixelFormat = .bgra8Unorm
        self.framebufferOnly = true
        self.isPaused = true  // Don't render in embedded view
        self.wantsLayer = true

        // Create command queue and texture cache (these are thread-safe)
        self.commandQueue = device.makeCommandQueue()

        var cache: CVMetalTextureCache?
        CVMetalTextureCacheCreate(nil, nil, device, nil, &cache)
        self.textureCache = cache

        // Setup render pipeline
        setupPipeline(device: device)

        // Create overlay window on main thread
        if Thread.isMainThread {
            createOverlayWindow()
        } else {
            DispatchQueue.main.async { [weak self] in
                self?.createOverlayWindow()
            }
        }
    }

    private var storedDevice: MTLDevice

    private func createOverlayWindow() {
        guard let device = self.device else { return }

        // Create a borderless, transparent window
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 320, height: 240),
            styleMask: [.borderless],
            backing: .buffered,
            defer: false
        )
        window.isOpaque = false
        window.backgroundColor = .clear
        window.level = .floating
        window.ignoresMouseEvents = true  // Let clicks pass through to Avalonia
        window.hasShadow = false
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]

        // Create the actual MTKView for the overlay
        let mtkView = MTKView(frame: window.contentView!.bounds, device: device)
        mtkView.colorPixelFormat = .bgra8Unorm
        mtkView.framebufferOnly = true
        mtkView.isPaused = false
        mtkView.enableSetNeedsDisplay = false
        mtkView.preferredFramesPerSecond = 60
        mtkView.clearColor = MTLClearColor(red: 0, green: 0.5, blue: 1.0, alpha: 1)  // Blue
        mtkView.delegate = self
        mtkView.autoresizingMask = [.width, .height]

        window.contentView?.addSubview(mtkView)

        self.overlayWindow = window
        self.overlayView = mtkView
    }

    private func setupPipeline(device: MTLDevice) {
        let shaderSource = """
        #include <metal_stdlib>
        using namespace metal;

        struct VertexOut {
            float4 position [[position]];
            float2 texCoord;
        };

        vertex VertexOut vertexShader(uint vertexID [[vertex_id]]) {
            float2 positions[6] = {
                float2(-1, -1), float2(1, -1), float2(-1, 1),
                float2(-1, 1), float2(1, -1), float2(1, 1)
            };
            float2 texCoords[6] = {
                float2(0, 1), float2(1, 1), float2(0, 0),
                float2(0, 0), float2(1, 1), float2(1, 0)
            };

            VertexOut out;
            out.position = float4(positions[vertexID], 0, 1);
            out.texCoord = texCoords[vertexID];
            return out;
        }

        fragment float4 fragmentShader(VertexOut in [[stage_in]],
                                       texture2d<float> yTexture [[texture(0)]],
                                       texture2d<float> uvTexture [[texture(1)]]) {
            constexpr sampler textureSampler(mag_filter::linear, min_filter::linear);

            float y = yTexture.sample(textureSampler, in.texCoord).r;
            float2 uv = uvTexture.sample(textureSampler, in.texCoord).rg;

            y = (y - 0.0625) * 1.164;
            float u = uv.r - 0.5;
            float v = uv.g - 0.5;

            float r = y + 1.596 * v;
            float g = y - 0.391 * u - 0.813 * v;
            float b = y + 2.018 * u;

            return float4(saturate(r), saturate(g), saturate(b), 1.0);
        }
        """

        do {
            let library = try device.makeLibrary(source: shaderSource, options: nil)
            let descriptor = MTLRenderPipelineDescriptor()
            descriptor.vertexFunction = library.makeFunction(name: "vertexShader")
            descriptor.fragmentFunction = library.makeFunction(name: "fragmentShader")
            descriptor.colorAttachments[0].pixelFormat = .bgra8Unorm
            pipelineState = try device.makeRenderPipelineState(descriptor: descriptor)
        } catch {
            // Pipeline creation failed - rendering will fall back to clear color only
        }
    }

    required init(coder: NSCoder) {
        fatalError("init(coder:) not implemented")
    }

    // MTKViewDelegate - called every frame by the overlay MTKView
    func draw(in view: MTKView) {
        // Use the overlay view's drawable
        guard let overlayView = overlayView,
              let drawable = overlayView.currentDrawable,
              let commandQueue = commandQueue,
              let commandBuffer = commandQueue.makeCommandBuffer() else {
            return
        }

        let renderPassDescriptor = MTLRenderPassDescriptor()
        renderPassDescriptor.colorAttachments[0].texture = drawable.texture
        renderPassDescriptor.colorAttachments[0].loadAction = .clear
        renderPassDescriptor.colorAttachments[0].storeAction = .store
        renderPassDescriptor.colorAttachments[0].clearColor = overlayView.clearColor

        guard let encoder = commandBuffer.makeRenderCommandEncoder(descriptor: renderPassDescriptor) else {
            return
        }

        // If we have a pixel buffer, render it
        if let pixelBuffer = pendingPixelBuffer,
           let pipelineState = pipelineState,
           let textureCache = textureCache {
            let width = CVPixelBufferGetWidth(pixelBuffer)
            let height = CVPixelBufferGetHeight(pixelBuffer)

            // Create Y texture
            var yTextureRef: CVMetalTexture?
            CVMetalTextureCacheCreateTextureFromImage(nil, textureCache, pixelBuffer, nil,
                .r8Unorm, width, height, 0, &yTextureRef)

            // Create UV texture
            var uvTextureRef: CVMetalTexture?
            CVMetalTextureCacheCreateTextureFromImage(nil, textureCache, pixelBuffer, nil,
                .rg8Unorm, width / 2, height / 2, 1, &uvTextureRef)

            if let yTexRef = yTextureRef, let uvTexRef = uvTextureRef,
               let yTexture = CVMetalTextureGetTexture(yTexRef),
               let uvTexture = CVMetalTextureGetTexture(uvTexRef) {
                encoder.setRenderPipelineState(pipelineState)
                encoder.setFragmentTexture(yTexture, index: 0)
                encoder.setFragmentTexture(uvTexture, index: 1)
                encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 6)
            }
        }

        encoder.endEncoding()
        commandBuffer.present(drawable)
        commandBuffer.commit()
    }

    func mtkView(_ view: MTKView, drawableSizeWillChange size: CGSize) {
        // Handle size changes if needed
    }

    /// Set the pixel buffer to render
    func setPixelBuffer(_ pixelBuffer: CVPixelBuffer) {
        pendingPixelBuffer = pixelBuffer
        // Store video dimensions for aspect ratio calculation
        videoWidth = CVPixelBufferGetWidth(pixelBuffer)
        videoHeight = CVPixelBufferGetHeight(pixelBuffer)
    }

    override func setFrameSize(_ newSize: NSSize) {
        targetSize = newSize
        super.setFrameSize(newSize)
        adjustFrameForSuperview()
    }

    override func viewDidMoveToSuperview() {
        super.viewDidMoveToSuperview()
        if superview != nil {
            adjustFrameForSuperview()
        }
    }

    private var positionTimer: Timer?

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if let parentWindow = window {
            // Make the overlay a child of the parent window
            if let overlay = overlayWindow {
                parentWindow.addChildWindow(overlay, ordered: .above)
                overlay.orderFront(nil)
            }

            // Start a timer to continuously update overlay position
            if positionTimer == nil {
                positionTimer = Timer.scheduledTimer(withTimeInterval: 1.0/60.0, repeats: true) { [weak self] _ in
                    self?.updateOverlayPosition()
                }
                RunLoop.current.add(positionTimer!, forMode: .common)
            }

            updateOverlayPosition()
        } else {
            // Detach overlay when removed from window
            if let overlay = overlayWindow {
                overlay.parent?.removeChildWindow(overlay)
                overlay.orderOut(nil)
            }
            positionTimer?.invalidate()
            positionTimer = nil
        }
    }

    private func updateOverlayPosition() {
        guard let parentWindow = window,
              let overlay = overlayWindow else { return }

        // Convert our frame to screen coordinates
        let frameInWindow = convert(bounds, to: nil)
        let containerFrame = parentWindow.convertToScreen(frameInWindow)

        // Calculate aspect-ratio-correct frame within the container
        let overlayFrame: NSRect
        if videoWidth > 0 && videoHeight > 0 {
            let videoAspect = CGFloat(videoWidth) / CGFloat(videoHeight)
            let containerAspect = containerFrame.width / containerFrame.height

            var videoRect: NSRect
            if videoAspect > containerAspect {
                // Video is wider than container - fit to width, letterbox top/bottom
                let width = containerFrame.width
                let height = width / videoAspect
                let y = containerFrame.origin.y + (containerFrame.height - height) / 2
                videoRect = NSRect(x: containerFrame.origin.x, y: y, width: width, height: height)
            } else {
                // Video is taller than container - fit to height, pillarbox left/right
                let height = containerFrame.height
                let width = height * videoAspect
                let x = containerFrame.origin.x + (containerFrame.width - width) / 2
                videoRect = NSRect(x: x, y: containerFrame.origin.y, width: width, height: height)
            }
            overlayFrame = videoRect
        } else {
            // No video dimensions yet, use full container
            overlayFrame = containerFrame
        }

        // Update overlay window position and size
        overlay.setFrame(overlayFrame, display: false)
    }

    private func adjustFrameForSuperview() {
        guard let superview = superview, targetSize.width > 0, targetSize.height > 0 else { return }

        let boundsInWindow = superview.convert(bounds, to: nil)
        if boundsInWindow.origin.y < 0 {
            let offsetNeeded = -boundsInWindow.origin.y

            if let grandparent = superview.superview, grandparent.isFlipped {
                var superFrame = superview.frame
                superFrame.origin.y -= offsetNeeded
                superview.frame = superFrame
            }
        }

        let newFrame = NSRect(x: 0, y: 0, width: targetSize.width, height: targetSize.height)
        if frame != newFrame {
            frame = newFrame
        }
    }

    override func resizeSubviews(withOldSize oldSize: NSSize) {
        super.resizeSubviews(withOldSize: oldSize)
        adjustFrameForSuperview()
    }
}
