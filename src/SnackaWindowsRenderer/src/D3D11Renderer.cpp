#include "D3D11Renderer.h"
#include <iostream>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d3dcompiler.lib")

// Static members
ATOM D3D11Renderer::s_windowClassAtom = 0;
bool D3D11Renderer::s_windowClassRegistered = false;

// Vertex structure for fullscreen quad
struct Vertex {
    float x, y;
    float u, v;
};

// NV12 to RGB pixel shader (HLSL)
static const char* s_pixelShaderSource = R"(
Texture2D<float> yTexture : register(t0);
Texture2D<float2> uvTexture : register(t1);
SamplerState samplerState : register(s0);

struct PS_INPUT {
    float4 position : SV_POSITION;
    float2 texCoord : TEXCOORD0;
};

float4 main(PS_INPUT input) : SV_TARGET {
    float y = yTexture.Sample(samplerState, input.texCoord);
    float2 uv = uvTexture.Sample(samplerState, input.texCoord);

    // BT.601 video range conversion
    y = (y - 0.0625) * 1.164;
    float u = uv.x - 0.5;
    float v = uv.y - 0.5;

    float r = y + 1.596 * v;
    float g = y - 0.391 * u - 0.813 * v;
    float b = y + 2.018 * u;

    return float4(saturate(r), saturate(g), saturate(b), 1.0);
}
)";

// Vertex shader (HLSL)
static const char* s_vertexShaderSource = R"(
struct VS_INPUT {
    float2 position : POSITION;
    float2 texCoord : TEXCOORD0;
};

struct VS_OUTPUT {
    float4 position : SV_POSITION;
    float2 texCoord : TEXCOORD0;
};

VS_OUTPUT main(VS_INPUT input) {
    VS_OUTPUT output;
    output.position = float4(input.position, 0.0, 1.0);
    output.texCoord = input.texCoord;
    return output;
}
)";

// Window procedure
static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
        case WM_CLOSE:
            // Don't close - we're managed by the parent
            return 0;
        case WM_ERASEBKGND:
            return 1;  // Prevent flickering
        default:
            return DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}

D3D11Renderer::D3D11Renderer(ID3D11Device* device, ID3D11DeviceContext* context)
    : m_device(device), m_context(context) {
}

D3D11Renderer::~D3D11Renderer() {
    Cleanup();
}

void D3D11Renderer::Cleanup() {
    if (m_stagingTexture) { m_stagingTexture->Release(); m_stagingTexture = nullptr; }
    if (m_gpuNV12Texture) { m_gpuNV12Texture->Release(); m_gpuNV12Texture = nullptr; }
    if (m_videoProcessor) { m_videoProcessor->Release(); m_videoProcessor = nullptr; }
    if (m_videoProcessorEnum) { m_videoProcessorEnum->Release(); m_videoProcessorEnum = nullptr; }
    if (m_videoContext) { m_videoContext->Release(); m_videoContext = nullptr; }
    if (m_videoDevice) { m_videoDevice->Release(); m_videoDevice = nullptr; }

    if (m_vertexBuffer) { m_vertexBuffer->Release(); m_vertexBuffer = nullptr; }
    if (m_sampler) { m_sampler->Release(); m_sampler = nullptr; }
    if (m_inputLayout) { m_inputLayout->Release(); m_inputLayout = nullptr; }
    if (m_pixelShader) { m_pixelShader->Release(); m_pixelShader = nullptr; }
    if (m_vertexShader) { m_vertexShader->Release(); m_vertexShader = nullptr; }
    if (m_renderTarget) { m_renderTarget->Release(); m_renderTarget = nullptr; }
    if (m_swapChain) { m_swapChain->Release(); m_swapChain = nullptr; }

    if (m_hwnd) {
        DestroyWindow(m_hwnd);
        m_hwnd = nullptr;
    }
}

bool D3D11Renderer::Initialize(int width, int height) {
    m_width = width;
    m_height = height;

    if (!CreateOverlayWindow(width, height, nullptr)) {
        std::cerr << "D3D11Renderer: Failed to create overlay window" << std::endl;
        return false;
    }

    // Don't create swap chain here - wait until window is reparented
    // InitializeSwapChain() will be called after reparenting

    std::cout << "D3D11Renderer: Initialized " << width << "x" << height << " (swap chain pending)" << std::endl;
    return true;
}

bool D3D11Renderer::InitializeWithParent(HWND parentHwnd, int width, int height) {
    m_width = width;
    m_height = height;

    if (!CreateOverlayWindow(width, height, parentHwnd)) {
        std::cerr << "D3D11Renderer: Failed to create child window" << std::endl;
        return false;
    }

    // Create swap chain immediately since window is already a child
    if (!CreateSwapChain()) {
        std::cerr << "D3D11Renderer: Failed to create swap chain" << std::endl;
        return false;
    }

    if (!CreateRenderResources()) {
        std::cerr << "D3D11Renderer: Failed to create render resources" << std::endl;
        return false;
    }

    std::cout << "D3D11Renderer: Initialized with parent " << width << "x" << height << std::endl;
    return true;
}

bool D3D11Renderer::InitializeSwapChain() {
    if (m_swapChain) {
        std::cout << "D3D11Renderer: Swap chain already exists" << std::endl;
        return true;
    }

    if (!CreateSwapChain()) {
        std::cerr << "D3D11Renderer: Failed to create swap chain" << std::endl;
        return false;
    }

    if (!CreateRenderResources()) {
        std::cerr << "D3D11Renderer: Failed to create render resources" << std::endl;
        return false;
    }

    std::cout << "D3D11Renderer: Swap chain initialized" << std::endl;
    return true;
}

bool D3D11Renderer::CreateOverlayWindow(int width, int height, HWND parentHwnd) {
    HINSTANCE hInstance = GetModuleHandle(nullptr);

    // Register window class if not already done
    if (!s_windowClassRegistered) {
        WNDCLASSEXW wc = {};
        wc.cbSize = sizeof(WNDCLASSEXW);
        wc.style = CS_HREDRAW | CS_VREDRAW;
        wc.lpfnWndProc = WndProc;
        wc.hInstance = hInstance;
        wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
        wc.hbrBackground = (HBRUSH)GetStockObject(BLACK_BRUSH);
        wc.lpszClassName = L"SnackaVideoOverlay";

        s_windowClassAtom = RegisterClassExW(&wc);
        if (s_windowClassAtom == 0) {
            std::cerr << "D3D11Renderer: RegisterClassExW failed" << std::endl;
            return false;
        }
        s_windowClassRegistered = true;
    }

    // Create window for video rendering
    // If parent is provided, create as child window; otherwise create as popup
    DWORD style = parentHwnd ? (WS_CHILD | WS_VISIBLE) : (WS_POPUP | WS_VISIBLE);

    m_hwnd = CreateWindowExW(
        0,
        L"SnackaVideoOverlay",
        L"Video Preview",
        style,
        0, 0,
        width, height,
        parentHwnd,
        nullptr,
        hInstance,
        nullptr
    );

    if (m_hwnd) {
        std::cout << "D3D11Renderer: Created " << (parentHwnd ? "child" : "popup")
                  << " window HWND=0x" << std::hex << (uintptr_t)m_hwnd << std::dec
                  << " size " << width << "x" << height;
        if (parentHwnd) {
            std::cout << " parent=0x" << std::hex << (uintptr_t)parentHwnd << std::dec;
        }
        std::cout << std::endl;
    }

    if (!m_hwnd) {
        std::cerr << "D3D11Renderer: CreateWindowExW failed: " << GetLastError() << std::endl;
        return false;
    }

    return true;
}

bool D3D11Renderer::CreateSwapChain() {
    // Get DXGI factory
    IDXGIDevice* dxgiDevice = nullptr;
    HRESULT hr = m_device->QueryInterface(IID_PPV_ARGS(&dxgiDevice));
    if (FAILED(hr)) return false;

    IDXGIAdapter* adapter = nullptr;
    hr = dxgiDevice->GetAdapter(&adapter);
    dxgiDevice->Release();
    if (FAILED(hr)) return false;

    IDXGIFactory2* factory = nullptr;
    hr = adapter->GetParent(IID_PPV_ARGS(&factory));
    adapter->Release();
    if (FAILED(hr)) return false;

    // Create swap chain
    DXGI_SWAP_CHAIN_DESC1 swapChainDesc = {};
    swapChainDesc.Width = m_width;
    swapChainDesc.Height = m_height;
    swapChainDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    swapChainDesc.Stereo = FALSE;
    swapChainDesc.SampleDesc.Count = 1;
    swapChainDesc.SampleDesc.Quality = 0;
    swapChainDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    swapChainDesc.BufferCount = 2;
    swapChainDesc.Scaling = DXGI_SCALING_STRETCH;
    swapChainDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
    swapChainDesc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    swapChainDesc.Flags = 0;

    hr = factory->CreateSwapChainForHwnd(
        m_device,
        m_hwnd,
        &swapChainDesc,
        nullptr,
        nullptr,
        &m_swapChain
    );
    factory->Release();

    if (FAILED(hr)) {
        std::cerr << "D3D11Renderer: CreateSwapChainForHwnd failed: " << std::hex << hr << std::endl;
        return false;
    }

    // Get back buffer and create render target view
    ID3D11Texture2D* backBuffer = nullptr;
    hr = m_swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
    if (FAILED(hr)) return false;

    hr = m_device->CreateRenderTargetView(backBuffer, nullptr, &m_renderTarget);
    backBuffer->Release();

    if (FAILED(hr)) {
        std::cerr << "D3D11Renderer: CreateRenderTargetView failed" << std::endl;
        return false;
    }

    // Create video device and context for NV12 processing
    hr = m_device->QueryInterface(IID_PPV_ARGS(&m_videoDevice));
    if (SUCCEEDED(hr)) {
        m_context->QueryInterface(IID_PPV_ARGS(&m_videoContext));
    }

    return true;
}

ID3DBlob* D3D11Renderer::CompileShader(const char* source, const char* entryPoint, const char* target) {
    ID3DBlob* blob = nullptr;
    ID3DBlob* errorBlob = nullptr;

    HRESULT hr = D3DCompile(
        source,
        strlen(source),
        nullptr,
        nullptr,
        nullptr,
        entryPoint,
        target,
        D3DCOMPILE_OPTIMIZATION_LEVEL3,
        0,
        &blob,
        &errorBlob
    );

    if (FAILED(hr)) {
        if (errorBlob) {
            std::cerr << "D3D11Renderer: Shader compile error: "
                      << (char*)errorBlob->GetBufferPointer() << std::endl;
            errorBlob->Release();
        }
        return nullptr;
    }

    if (errorBlob) errorBlob->Release();
    return blob;
}

bool D3D11Renderer::CreateRenderResources() {
    // Compile vertex shader
    ID3DBlob* vsBlob = CompileShader(s_vertexShaderSource, "main", "vs_5_0");
    if (!vsBlob) return false;

    HRESULT hr = m_device->CreateVertexShader(
        vsBlob->GetBufferPointer(),
        vsBlob->GetBufferSize(),
        nullptr,
        &m_vertexShader
    );

    if (FAILED(hr)) {
        vsBlob->Release();
        return false;
    }

    // Create input layout
    D3D11_INPUT_ELEMENT_DESC inputDesc[] = {
        { "POSITION", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0 },
        { "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 8, D3D11_INPUT_PER_VERTEX_DATA, 0 }
    };

    hr = m_device->CreateInputLayout(
        inputDesc,
        2,
        vsBlob->GetBufferPointer(),
        vsBlob->GetBufferSize(),
        &m_inputLayout
    );
    vsBlob->Release();

    if (FAILED(hr)) return false;

    // Compile pixel shader
    ID3DBlob* psBlob = CompileShader(s_pixelShaderSource, "main", "ps_5_0");
    if (!psBlob) return false;

    hr = m_device->CreatePixelShader(
        psBlob->GetBufferPointer(),
        psBlob->GetBufferSize(),
        nullptr,
        &m_pixelShader
    );
    psBlob->Release();

    if (FAILED(hr)) return false;

    // Create sampler state
    D3D11_SAMPLER_DESC samplerDesc = {};
    samplerDesc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    samplerDesc.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
    samplerDesc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
    samplerDesc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;

    hr = m_device->CreateSamplerState(&samplerDesc, &m_sampler);
    if (FAILED(hr)) return false;

    // Create vertex buffer for fullscreen quad
    Vertex vertices[] = {
        { -1.0f, -1.0f,  0.0f, 1.0f },  // Bottom-left
        { -1.0f,  1.0f,  0.0f, 0.0f },  // Top-left
        {  1.0f, -1.0f,  1.0f, 1.0f },  // Bottom-right
        {  1.0f,  1.0f,  1.0f, 0.0f }   // Top-right
    };

    D3D11_BUFFER_DESC bufferDesc = {};
    bufferDesc.ByteWidth = sizeof(vertices);
    bufferDesc.Usage = D3D11_USAGE_DEFAULT;
    bufferDesc.BindFlags = D3D11_BIND_VERTEX_BUFFER;

    D3D11_SUBRESOURCE_DATA initData = {};
    initData.pSysMem = vertices;

    hr = m_device->CreateBuffer(&bufferDesc, &initData, &m_vertexBuffer);
    if (FAILED(hr)) return false;

    return true;
}

void D3D11Renderer::RenderNV12Texture(ID3D11Texture2D* texture) {
    // Skip rendering until swap chain is created (after window reparenting)
    if (!m_swapChain) {
        static int skippedFrames = 0;
        skippedFrames++;
        if (skippedFrames <= 5 || skippedFrames % 100 == 0) {
            std::cerr << "D3D11Renderer::RenderNV12Texture: skipping frame " << skippedFrames << " (no swap chain yet)" << std::endl;
        }
        return;
    }

    if (!m_renderTarget || !texture) {
        std::cerr << "D3D11Renderer::RenderNV12Texture: null renderTarget or texture" << std::endl;
        return;
    }

    static int frameCount = 0;
    frameCount++;
    if (frameCount <= 5 || frameCount % 100 == 0) {
        std::cerr << "D3D11Renderer::RenderNV12Texture: frame " << frameCount << std::endl;
    }

    // Get texture description
    D3D11_TEXTURE2D_DESC texDesc;
    texture->GetDesc(&texDesc);

    // Create shader resource views for Y and UV planes
    // For NV12, we need two views: one for Y (R8), one for UV (R8G8)

    // Y plane view
    D3D11_SHADER_RESOURCE_VIEW_DESC yViewDesc = {};
    yViewDesc.Format = DXGI_FORMAT_R8_UNORM;
    yViewDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    yViewDesc.Texture2D.MipLevels = 1;

    ID3D11ShaderResourceView* yView = nullptr;
    HRESULT hr = m_device->CreateShaderResourceView(texture, &yViewDesc, &yView);
    if (FAILED(hr)) {
        // NV12 textures from hardware decoder can't create SRVs directly - use video processor
        // This is expected behavior, only log once
        static bool loggedOnce = false;
        if (!loggedOnce) {
            std::cerr << "D3D11Renderer: Using video processor for NV12 conversion" << std::endl;
            loggedOnce = true;
        }
        RenderUsingVideoProcessor(texture);
        return;
    }

    // UV plane view
    D3D11_SHADER_RESOURCE_VIEW_DESC uvViewDesc = {};
    uvViewDesc.Format = DXGI_FORMAT_R8G8_UNORM;
    uvViewDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    uvViewDesc.Texture2D.MipLevels = 1;

    ID3D11ShaderResourceView* uvView = nullptr;
    hr = m_device->CreateShaderResourceView(texture, &uvViewDesc, &uvView);
    if (FAILED(hr)) {
        yView->Release();
        RenderUsingVideoProcessor(texture);
        return;
    }

    // Set render target
    m_context->OMSetRenderTargets(1, &m_renderTarget, nullptr);

    // Set viewport
    D3D11_VIEWPORT viewport = {};
    viewport.Width = (float)m_width;
    viewport.Height = (float)m_height;
    viewport.MaxDepth = 1.0f;
    m_context->RSSetViewports(1, &viewport);

    // Clear
    float clearColor[] = { 0.0f, 0.0f, 0.0f, 1.0f };
    m_context->ClearRenderTargetView(m_renderTarget, clearColor);

    // Set shaders
    m_context->VSSetShader(m_vertexShader, nullptr, 0);
    m_context->PSSetShader(m_pixelShader, nullptr, 0);
    m_context->IASetInputLayout(m_inputLayout);

    // Set textures
    ID3D11ShaderResourceView* views[] = { yView, uvView };
    m_context->PSSetShaderResources(0, 2, views);
    m_context->PSSetSamplers(0, 1, &m_sampler);

    // Set vertex buffer
    UINT stride = sizeof(Vertex);
    UINT offset = 0;
    m_context->IASetVertexBuffers(0, 1, &m_vertexBuffer, &stride, &offset);
    m_context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);

    // Draw
    m_context->Draw(4, 0);

    // Cleanup
    yView->Release();
    uvView->Release();

    // Present (no VSync to avoid blocking)
    hr = m_swapChain->Present(0, 0);
    if (FAILED(hr)) {
        std::cerr << "D3D11Renderer: Present failed: 0x" << std::hex << hr << std::dec << std::endl;
    } else if (!m_windowShown) {
        // Show window after first successful present
        ShowWindow(m_hwnd, SW_SHOW);
        m_windowShown = true;
        std::cout << "D3D11Renderer: Window shown after first frame" << std::endl;
    }
}

void D3D11Renderer::RenderUsingVideoProcessor(ID3D11Texture2D* texture) {
    static int vpFrameCount = 0;
    vpFrameCount++;
    if (vpFrameCount <= 5 || vpFrameCount % 100 == 0) {
        std::cerr << "D3D11Renderer::RenderUsingVideoProcessor: frame " << vpFrameCount << std::endl;
    }

    // Fallback: Use D3D11 video processor for NV12 to BGRA conversion
    if (!m_videoDevice || !m_videoContext) {
        std::cerr << "D3D11Renderer: Video processor not available" << std::endl;
        return;
    }

    // Create video processor if needed
    if (!m_videoProcessor) {
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC contentDesc = {};
        contentDesc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        contentDesc.InputWidth = m_width;
        contentDesc.InputHeight = m_height;
        contentDesc.OutputWidth = m_width;
        contentDesc.OutputHeight = m_height;
        contentDesc.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;

        HRESULT hr = m_videoDevice->CreateVideoProcessorEnumerator(&contentDesc, &m_videoProcessorEnum);
        if (FAILED(hr)) return;

        hr = m_videoDevice->CreateVideoProcessor(m_videoProcessorEnum, 0, &m_videoProcessor);
        if (FAILED(hr)) return;

        // Set color space: input is NV12 with studio range (BT.601/709), output is full RGB
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE inputColorSpace = {};
        inputColorSpace.Usage = 0;  // Playback
        inputColorSpace.RGB_Range = 0;  // Not applicable for YCbCr input
        inputColorSpace.YCbCr_Matrix = 1;  // BT.709
        inputColorSpace.YCbCr_xvYCC = 0;  // Conventional YCbCr
        inputColorSpace.Nominal_Range = 1;  // Studio range (16-235)
        m_videoContext->VideoProcessorSetStreamColorSpace(m_videoProcessor, 0, &inputColorSpace);

        D3D11_VIDEO_PROCESSOR_COLOR_SPACE outputColorSpace = {};
        outputColorSpace.Usage = 0;  // Playback
        outputColorSpace.RGB_Range = 0;  // Full range RGB (0-255)
        outputColorSpace.YCbCr_Matrix = 1;  // BT.709
        outputColorSpace.YCbCr_xvYCC = 0;
        outputColorSpace.Nominal_Range = 2;  // Full range (0-255)
        m_videoContext->VideoProcessorSetOutputColorSpace(m_videoProcessor, &outputColorSpace);

        std::cout << "D3D11Renderer: Video processor configured with BT.709 studio->full range" << std::endl;
    }

    // Create input view
    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inputViewDesc = {};
    inputViewDesc.FourCC = 0;
    inputViewDesc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
    inputViewDesc.Texture2D.MipSlice = 0;

    ID3D11VideoProcessorInputView* inputView = nullptr;
    HRESULT hr = m_videoDevice->CreateVideoProcessorInputView(
        texture, m_videoProcessorEnum, &inputViewDesc, &inputView);
    if (FAILED(hr)) {
        std::cerr << "D3D11Renderer: CreateVideoProcessorInputView failed: 0x" << std::hex << hr << std::dec << std::endl;
        return;
    }

    // Get back buffer for output
    ID3D11Texture2D* backBuffer = nullptr;
    m_swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));

    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outputViewDesc = {};
    outputViewDesc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;

    ID3D11VideoProcessorOutputView* outputView = nullptr;
    hr = m_videoDevice->CreateVideoProcessorOutputView(
        backBuffer, m_videoProcessorEnum, &outputViewDesc, &outputView);
    backBuffer->Release();

    if (FAILED(hr)) {
        std::cerr << "D3D11Renderer: CreateVideoProcessorOutputView failed: 0x" << std::hex << hr << std::dec << std::endl;
        inputView->Release();
        return;
    }

    // Process
    D3D11_VIDEO_PROCESSOR_STREAM stream = {};
    stream.Enable = TRUE;
    stream.pInputSurface = inputView;

    hr = m_videoContext->VideoProcessorBlt(m_videoProcessor, outputView, 0, 1, &stream);
    if (FAILED(hr)) {
        std::cerr << "D3D11Renderer: VideoProcessorBlt failed: 0x" << std::hex << hr << std::dec << std::endl;
    }

    inputView->Release();
    outputView->Release();

    // Present (no VSync to avoid blocking)
    hr = m_swapChain->Present(0, 0);
    if (FAILED(hr)) {
        std::cerr << "D3D11Renderer: Present failed in VideoProcessor path: 0x" << std::hex << hr << std::dec << std::endl;
    } else if (!m_windowShown) {
        // Show window after first successful present
        ShowWindow(m_hwnd, SW_SHOW);
        m_windowShown = true;
        std::cout << "D3D11Renderer: Window shown after first frame" << std::endl;
    }
    // Note: Message pump is in RenderNV12Texture, not duplicated here
}

bool D3D11Renderer::RecreateSwapChain() {
    std::cout << "D3D11Renderer: Recreating swap chain after reparent" << std::endl;

    // Release old swap chain resources
    if (m_renderTarget) {
        m_renderTarget->Release();
        m_renderTarget = nullptr;
    }
    if (m_swapChain) {
        m_swapChain->Release();
        m_swapChain = nullptr;
    }

    // Release video processor (will be recreated on next use)
    if (m_videoProcessor) {
        m_videoProcessor->Release();
        m_videoProcessor = nullptr;
    }
    if (m_videoProcessorEnum) {
        m_videoProcessorEnum->Release();
        m_videoProcessorEnum = nullptr;
    }

    // Recreate swap chain
    if (!CreateSwapChain()) {
        std::cerr << "D3D11Renderer: Failed to recreate swap chain" << std::endl;
        return false;
    }

    std::cout << "D3D11Renderer: Swap chain recreated successfully" << std::endl;
    return true;
}

void D3D11Renderer::SetDisplaySize(int width, int height) {
    if (width == m_width && height == m_height) return;

    m_width = width;
    m_height = height;

    // Resize window
    if (m_hwnd) {
        SetWindowPos(m_hwnd, nullptr, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER);
    }

    // Resize swap chain
    if (m_swapChain) {
        // Release render target first
        if (m_renderTarget) {
            m_renderTarget->Release();
            m_renderTarget = nullptr;
        }

        HRESULT hr = m_swapChain->ResizeBuffers(0, width, height, DXGI_FORMAT_UNKNOWN, 0);
        if (SUCCEEDED(hr)) {
            // Recreate render target
            ID3D11Texture2D* backBuffer = nullptr;
            hr = m_swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
            if (SUCCEEDED(hr)) {
                m_device->CreateRenderTargetView(backBuffer, nullptr, &m_renderTarget);
                backBuffer->Release();
            }
        }
    }
}

void D3D11Renderer::RenderNV12Data(const uint8_t* data, int dataSize, int width, int height) {
    if (!m_device || !m_context || !data) return;

    // Expected NV12 size: width * height * 1.5
    int expectedSize = width * height * 3 / 2;
    if (dataSize < expectedSize) {
        std::cerr << "D3D11Renderer: NV12 data too small (got " << dataSize << ", expected " << expectedSize << ")" << std::endl;
        return;
    }

    // Create or recreate textures if dimensions changed
    if (!m_stagingTexture || m_stagingWidth != width || m_stagingHeight != height) {
        if (m_stagingTexture) {
            m_stagingTexture->Release();
            m_stagingTexture = nullptr;
        }

        // Create staging texture for CPU write
        D3D11_TEXTURE2D_DESC stagingDesc = {};
        stagingDesc.Width = width;
        stagingDesc.Height = height;
        stagingDesc.MipLevels = 1;
        stagingDesc.ArraySize = 1;
        stagingDesc.Format = DXGI_FORMAT_NV12;
        stagingDesc.SampleDesc.Count = 1;
        stagingDesc.Usage = D3D11_USAGE_STAGING;
        stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        stagingDesc.BindFlags = 0;

        HRESULT hr = m_device->CreateTexture2D(&stagingDesc, nullptr, &m_stagingTexture);
        if (FAILED(hr)) {
            std::cerr << "D3D11Renderer: Failed to create staging texture: " << std::hex << hr << std::endl;
            return;
        }

        // Create GPU texture for rendering
        if (m_gpuNV12Texture) {
            m_gpuNV12Texture->Release();
            m_gpuNV12Texture = nullptr;
        }

        D3D11_TEXTURE2D_DESC gpuDesc = stagingDesc;
        gpuDesc.Usage = D3D11_USAGE_DEFAULT;
        gpuDesc.CPUAccessFlags = 0;
        gpuDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;

        hr = m_device->CreateTexture2D(&gpuDesc, nullptr, &m_gpuNV12Texture);
        if (FAILED(hr)) {
            std::cerr << "D3D11Renderer: Failed to create GPU NV12 texture: " << std::hex << hr << std::endl;
            m_stagingTexture->Release();
            m_stagingTexture = nullptr;
            return;
        }

        m_stagingWidth = width;
        m_stagingHeight = height;
        std::cout << "D3D11Renderer: Created NV12 textures " << width << "x" << height << std::endl;
    }

    // Map staging texture and copy NV12 data
    D3D11_MAPPED_SUBRESOURCE mapped;
    HRESULT hr = m_context->Map(m_stagingTexture, 0, D3D11_MAP_WRITE, 0, &mapped);
    if (FAILED(hr)) {
        std::cerr << "D3D11Renderer: Failed to map staging texture: " << std::hex << hr << std::endl;
        return;
    }

    // Copy Y plane
    uint8_t* dst = static_cast<uint8_t*>(mapped.pData);
    const uint8_t* srcY = data;
    for (int y = 0; y < height; y++) {
        memcpy(dst + y * mapped.RowPitch, srcY + y * width, width);
    }

    // Copy UV plane (height/2 rows after Y plane in the mapped memory)
    uint8_t* dstUV = dst + mapped.RowPitch * height;
    const uint8_t* srcUV = data + width * height;
    for (int y = 0; y < height / 2; y++) {
        memcpy(dstUV + y * mapped.RowPitch, srcUV + y * width, width);
    }

    m_context->Unmap(m_stagingTexture, 0);

    // Copy staging to GPU texture
    m_context->CopyResource(m_gpuNV12Texture, m_stagingTexture);

    // Render the GPU texture
    RenderNV12Texture(m_gpuNV12Texture);
}
