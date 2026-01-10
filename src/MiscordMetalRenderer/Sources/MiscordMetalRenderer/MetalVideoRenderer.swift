import Foundation
import Metal
import MetalKit
import AppKit
import QuartzCore

/// Metal-based video renderer for NV12 (YUV 4:2:0) to RGB conversion.
/// Performs YUV→RGB conversion on GPU using a fragment shader.
public class MetalVideoRenderer {
    private let device: MTLDevice
    private let commandQueue: MTLCommandQueue
    private let pipelineState: MTLRenderPipelineState

    private var yTexture: MTLTexture?
    private var uvTexture: MTLTexture?
    private var vertexBuffer: MTLBuffer?

    private var videoWidth: Int = 0
    private var videoHeight: Int = 0

    private let metalLayer: CAMetalLayer
    public let view: NSView

    // Vertex data for a full-screen quad (position + texCoord)
    private static let quadVertices: [Float] = [
        // Position (x, y)   TexCoord (u, v)
        -1.0, -1.0,          0.0, 1.0,  // Bottom-left
         1.0, -1.0,          1.0, 1.0,  // Bottom-right
        -1.0,  1.0,          0.0, 0.0,  // Top-left
         1.0,  1.0,          1.0, 0.0,  // Top-right
    ]

    public init?() {
        // Get Metal device
        guard let device = MTLCreateSystemDefaultDevice() else {
            fputs("MetalVideoRenderer: Failed to create Metal device\n", stderr)
            return nil
        }
        self.device = device

        // Create command queue
        guard let commandQueue = device.makeCommandQueue() else {
            fputs("MetalVideoRenderer: Failed to create command queue\n", stderr)
            return nil
        }
        self.commandQueue = commandQueue

        // Create Metal layer
        metalLayer = CAMetalLayer()
        metalLayer.device = device
        metalLayer.pixelFormat = .bgra8Unorm
        metalLayer.framebufferOnly = true
        metalLayer.displaySyncEnabled = false  // Don't wait for vsync - low latency

        // Create NSView with Metal layer
        view = NSView(frame: NSRect(x: 0, y: 0, width: 320, height: 240))
        view.wantsLayer = true
        view.layer = metalLayer

        // Create vertex buffer
        let vertexData = MetalVideoRenderer.quadVertices
        guard let vertexBuffer = device.makeBuffer(bytes: vertexData, length: vertexData.count * MemoryLayout<Float>.size, options: .storageModeShared) else {
            fputs("MetalVideoRenderer: Failed to create vertex buffer\n", stderr)
            return nil
        }
        self.vertexBuffer = vertexBuffer

        // Compile shaders and create pipeline
        guard let pipelineState = MetalVideoRenderer.createPipelineState(device: device) else {
            fputs("MetalVideoRenderer: Failed to create pipeline state\n", stderr)
            return nil
        }
        self.pipelineState = pipelineState

        fputs("MetalVideoRenderer: Initialized successfully\n", stderr)
    }

    private static func createPipelineState(device: MTLDevice) -> MTLRenderPipelineState? {
        // Metal shader source for YUV→RGB conversion
        let shaderSource = """
        #include <metal_stdlib>
        using namespace metal;

        struct VertexIn {
            float2 position [[attribute(0)]];
            float2 texCoord [[attribute(1)]];
        };

        struct VertexOut {
            float4 position [[position]];
            float2 texCoord;
        };

        vertex VertexOut vertexShader(uint vertexID [[vertex_id]],
                                       constant float4* vertices [[buffer(0)]]) {
            VertexOut out;
            float4 v = vertices[vertexID];
            out.position = float4(v.xy, 0.0, 1.0);
            out.texCoord = v.zw;
            return out;
        }

        fragment float4 fragmentShader(VertexOut in [[stage_in]],
                                        texture2d<float> yTexture [[texture(0)]],
                                        texture2d<float> uvTexture [[texture(1)]]) {
            constexpr sampler textureSampler(mag_filter::linear, min_filter::linear);

            // Sample Y and UV values
            float y = yTexture.sample(textureSampler, in.texCoord).r;
            float2 uv = uvTexture.sample(textureSampler, in.texCoord).rg;

            // NV12 uses video range (16-235 for Y, 16-240 for UV)
            // Convert to full range first
            y = (y - 0.0625) * 1.164;
            float u = uv.r - 0.5;
            float v = uv.g - 0.5;

            // BT.601 YUV to RGB conversion
            float r = y + 1.596 * v;
            float g = y - 0.391 * u - 0.813 * v;
            float b = y + 2.018 * u;

            return float4(r, g, b, 1.0);
        }
        """

        // Compile shader library
        guard let library = try? device.makeLibrary(source: shaderSource, options: nil) else {
            fputs("MetalVideoRenderer: Failed to compile shaders\n", stderr)
            return nil
        }

        guard let vertexFunction = library.makeFunction(name: "vertexShader"),
              let fragmentFunction = library.makeFunction(name: "fragmentShader") else {
            fputs("MetalVideoRenderer: Failed to get shader functions\n", stderr)
            return nil
        }

        // Create pipeline descriptor
        let pipelineDescriptor = MTLRenderPipelineDescriptor()
        pipelineDescriptor.vertexFunction = vertexFunction
        pipelineDescriptor.fragmentFunction = fragmentFunction
        pipelineDescriptor.colorAttachments[0].pixelFormat = .bgra8Unorm

        // Create pipeline state
        do {
            return try device.makeRenderPipelineState(descriptor: pipelineDescriptor)
        } catch {
            fputs("MetalVideoRenderer: Failed to create pipeline state: \(error)\n", stderr)
            return nil
        }
    }

    /// Initialize textures for the specified video dimensions.
    public func initialize(width: Int, height: Int) -> Bool {
        videoWidth = width
        videoHeight = height

        // Create Y texture (full resolution, single channel)
        let yDescriptor = MTLTextureDescriptor.texture2DDescriptor(
            pixelFormat: .r8Unorm,
            width: width,
            height: height,
            mipmapped: false
        )
        yDescriptor.usage = [.shaderRead]
        yDescriptor.storageMode = .shared

        guard let yTex = device.makeTexture(descriptor: yDescriptor) else {
            fputs("MetalVideoRenderer: Failed to create Y texture\n", stderr)
            return false
        }
        yTexture = yTex

        // Create UV texture (half resolution, two channels interleaved)
        let uvDescriptor = MTLTextureDescriptor.texture2DDescriptor(
            pixelFormat: .rg8Unorm,
            width: width / 2,
            height: height / 2,
            mipmapped: false
        )
        uvDescriptor.usage = [.shaderRead]
        uvDescriptor.storageMode = .shared

        guard let uvTex = device.makeTexture(descriptor: uvDescriptor) else {
            fputs("MetalVideoRenderer: Failed to create UV texture\n", stderr)
            return false
        }
        uvTexture = uvTex

        fputs("MetalVideoRenderer: Initialized textures for \(width)x\(height)\n", stderr)
        return true
    }

    /// Render a frame from NV12 data.
    public func renderFrame(_ nv12Data: UnsafePointer<UInt8>, length: Int) {
        guard let yTexture = yTexture,
              let uvTexture = uvTexture,
              let vertexBuffer = vertexBuffer else {
            return
        }

        let expectedLength = videoWidth * videoHeight * 3 / 2
        guard length >= expectedLength else {
            fputs("MetalVideoRenderer: Invalid NV12 data length: \(length), expected \(expectedLength)\n", stderr)
            return
        }

        // Update textures with NV12 data
        let yPlaneSize = videoWidth * videoHeight

        // Upload Y plane
        yTexture.replace(
            region: MTLRegionMake2D(0, 0, videoWidth, videoHeight),
            mipmapLevel: 0,
            withBytes: nv12Data,
            bytesPerRow: videoWidth
        )

        // Upload UV plane (interleaved, half resolution)
        uvTexture.replace(
            region: MTLRegionMake2D(0, 0, videoWidth / 2, videoHeight / 2),
            mipmapLevel: 0,
            withBytes: nv12Data + yPlaneSize,
            bytesPerRow: videoWidth  // UV is interleaved, so width bytes per row
        )

        // Get drawable and render
        guard let drawable = metalLayer.nextDrawable() else {
            return
        }

        guard let commandBuffer = commandQueue.makeCommandBuffer() else {
            return
        }

        let renderPassDescriptor = MTLRenderPassDescriptor()
        renderPassDescriptor.colorAttachments[0].texture = drawable.texture
        renderPassDescriptor.colorAttachments[0].loadAction = .clear
        renderPassDescriptor.colorAttachments[0].storeAction = .store
        renderPassDescriptor.colorAttachments[0].clearColor = MTLClearColorMake(0, 0, 0, 1)

        guard let renderEncoder = commandBuffer.makeRenderCommandEncoder(descriptor: renderPassDescriptor) else {
            return
        }

        renderEncoder.setRenderPipelineState(pipelineState)
        renderEncoder.setVertexBuffer(vertexBuffer, offset: 0, index: 0)
        renderEncoder.setFragmentTexture(yTexture, index: 0)
        renderEncoder.setFragmentTexture(uvTexture, index: 1)
        renderEncoder.drawPrimitives(type: .triangleStrip, vertexStart: 0, vertexCount: 4)
        renderEncoder.endEncoding()

        commandBuffer.present(drawable)
        commandBuffer.commit()
    }

    /// Set display size for the Metal layer.
    public func setDisplaySize(width: Int, height: Int) {
        metalLayer.frame = CGRect(x: 0, y: 0, width: width, height: height)
        metalLayer.drawableSize = CGSize(width: width, height: height)
    }

    deinit {
        fputs("MetalVideoRenderer: Destroyed\n", stderr)
    }
}
