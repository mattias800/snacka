#pragma once

#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <d3dcompiler.h>
#include <cstdint>

class D3D11Renderer {
public:
    D3D11Renderer(ID3D11Device* device, ID3D11DeviceContext* context);
    ~D3D11Renderer();

    // Non-copyable
    D3D11Renderer(const D3D11Renderer&) = delete;
    D3D11Renderer& operator=(const D3D11Renderer&) = delete;

    // Initialize the renderer with video dimensions
    bool Initialize(int width, int height);

    // Render an NV12 texture to the window
    void RenderNV12Texture(ID3D11Texture2D* texture);

    // Render raw NV12 data (software decode path)
    void RenderNV12Data(const uint8_t* data, int dataSize, int width, int height);

    // Get the window handle
    HWND GetHwnd() const { return m_hwnd; }

    // Set the display size
    void SetDisplaySize(int width, int height);

private:
    // Create the overlay window
    bool CreateOverlayWindow(int width, int height);

    // Create swap chain
    bool CreateSwapChain();

    // Create render resources (shaders, samplers, etc.)
    bool CreateRenderResources();

    // Compile shader from source
    ID3DBlob* CompileShader(const char* source, const char* entryPoint, const char* target);

    // Render using video processor (fallback)
    void RenderUsingVideoProcessor(ID3D11Texture2D* texture);

    // Cleanup
    void Cleanup();

private:
    // D3D11 (borrowed references, not owned)
    ID3D11Device* m_device = nullptr;
    ID3D11DeviceContext* m_context = nullptr;

    // Swap chain and render target
    IDXGISwapChain1* m_swapChain = nullptr;
    ID3D11RenderTargetView* m_renderTarget = nullptr;

    // Shaders
    ID3D11VertexShader* m_vertexShader = nullptr;
    ID3D11PixelShader* m_pixelShader = nullptr;
    ID3D11InputLayout* m_inputLayout = nullptr;

    // Sampler and vertex buffer
    ID3D11SamplerState* m_sampler = nullptr;
    ID3D11Buffer* m_vertexBuffer = nullptr;

    // Video processor for NV12 to BGRA conversion
    ID3D11VideoDevice* m_videoDevice = nullptr;
    ID3D11VideoContext* m_videoContext = nullptr;
    ID3D11VideoProcessor* m_videoProcessor = nullptr;
    ID3D11VideoProcessorEnumerator* m_videoProcessorEnum = nullptr;

    // Window
    HWND m_hwnd = nullptr;

    // Dimensions
    int m_width = 0;
    int m_height = 0;

    // Textures for software decode path
    ID3D11Texture2D* m_stagingTexture = nullptr;
    ID3D11Texture2D* m_gpuNV12Texture = nullptr;
    int m_stagingWidth = 0;
    int m_stagingHeight = 0;

    // Window visibility tracking
    bool m_windowShown = false;

    // Window class atom
    static ATOM s_windowClassAtom;
    static bool s_windowClassRegistered;
};
