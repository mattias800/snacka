# Windows Hardware Video Decoder Implementation Guide

This document provides a complete guide for implementing hardware-accelerated H264 video decoding on Windows using Media Foundation and Direct3D 11.

## Overview

The goal is to create a zero-copy video pipeline:
```
H264 NAL units → Media Foundation H264 Decoder → D3D11 Texture → Display
```

This matches the macOS implementation which uses:
```
H264 NAL units → VideoToolbox → CVPixelBuffer/Metal Texture → Display
```

## Architecture

### Components to Implement

1. **Native C++ Library** (`MiscordWindowsRenderer.dll`)
   - Media Foundation H264 decoder wrapper
   - D3D11 renderer with swap chain
   - C API for P/Invoke

2. **C# P/Invoke Wrapper** (`MediaFoundationDecoder.cs`)
   - Implements `IHardwareVideoDecoder` interface
   - Manages native library lifecycle

### Key Challenges (Solved on macOS, Apply Same Pattern)

1. **Avalonia NativeControlHost compositor issues**: The native D3D11 window may not composite properly inside Avalonia. Solution: Use an overlay window approach (child HWND floating above the Avalonia window).

2. **Memory management**: Ensure frame data is copied into buffers owned by the decoder, not referencing stack memory.

3. **Threading**: D3D11 device and swap chain must be created/used on appropriate threads.

## C API Specification

The native library must export these functions (matching macOS pattern):

```cpp
// MiscordWindowsRenderer.h

#ifdef __cplusplus
extern "C" {
#endif

// Opaque handle to decoder instance
typedef void* MFDecoderHandle;

// Create a new decoder instance
// Returns: Handle to decoder, or NULL on failure
__declspec(dllexport) MFDecoderHandle mf_decoder_create();

// Destroy a decoder instance
__declspec(dllexport) void mf_decoder_destroy(MFDecoderHandle decoder);

// Initialize decoder with video parameters
// spsData/ppsData: H264 parameter sets (without Annex B start codes)
// Returns: true on success
__declspec(dllexport) bool mf_decoder_initialize(
    MFDecoderHandle decoder,
    int width,
    int height,
    const uint8_t* spsData,
    int spsLength,
    const uint8_t* ppsData,
    int ppsLength
);

// Decode an H264 NAL unit and render to the D3D11 surface
// nalData: NAL unit bytes (without Annex B start code)
// isKeyframe: true if this is an IDR frame
// Returns: true on successful decode and render
__declspec(dllexport) bool mf_decoder_decode_and_render(
    MFDecoderHandle decoder,
    const uint8_t* nalData,
    int nalLength,
    bool isKeyframe
);

// Get the native window handle (HWND) for embedding
// Returns: HWND that can be used with Avalonia's NativeControlHost
__declspec(dllexport) void* mf_decoder_get_view(MFDecoderHandle decoder);

// Set the display size (for the renderer window)
__declspec(dllexport) void mf_decoder_set_display_size(
    MFDecoderHandle decoder,
    int width,
    int height
);

// Check if Media Foundation H264 decoding is available
__declspec(dllexport) bool mf_decoder_is_available();

#ifdef __cplusplus
}
#endif
```

## Native Implementation Details

### Required Windows APIs

```cpp
// Media Foundation for H264 decoding
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")

// Direct3D 11 for rendering
#include <d3d11.h>
#include <dxgi1_2.h>
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
```

### Decoder Class Structure

```cpp
class MediaFoundationDecoder {
private:
    // Media Foundation
    IMFTransform* m_decoder = nullptr;
    IMFDXGIDeviceManager* m_deviceManager = nullptr;
    UINT m_resetToken = 0;

    // D3D11
    ID3D11Device* m_device = nullptr;
    ID3D11DeviceContext* m_context = nullptr;
    IDXGISwapChain1* m_swapChain = nullptr;
    ID3D11RenderTargetView* m_renderTarget = nullptr;

    // Window
    HWND m_hwnd = nullptr;
    HWND m_parentHwnd = nullptr;  // For overlay approach

    // Video parameters
    int m_width = 0;
    int m_height = 0;
    std::vector<uint8_t> m_sps;
    std::vector<uint8_t> m_pps;

    // Shader resources for NV12 → RGB conversion
    ID3D11PixelShader* m_pixelShader = nullptr;
    ID3D11VertexShader* m_vertexShader = nullptr;
    ID3D11SamplerState* m_sampler = nullptr;

public:
    bool Initialize(int width, int height,
                    const uint8_t* sps, int spsLen,
                    const uint8_t* pps, int ppsLen);
    bool DecodeAndRender(const uint8_t* nal, int nalLen, bool isKeyframe);
    HWND GetView() const { return m_hwnd; }
    void SetDisplaySize(int width, int height);

private:
    bool CreateD3D11Device();
    bool CreateDecoder();
    bool CreateSwapChain();
    bool CreateShaders();
    void RenderFrame(ID3D11Texture2D* texture);
};
```

### Media Foundation Decoder Setup

```cpp
bool MediaFoundationDecoder::CreateDecoder() {
    // 1. Find the H264 hardware decoder MFT
    MFT_REGISTER_TYPE_INFO inputType = { MFMediaType_Video, MFVideoFormat_H264 };
    MFT_REGISTER_TYPE_INFO outputType = { MFMediaType_Video, MFVideoFormat_NV12 };

    IMFActivate** ppActivate = nullptr;
    UINT32 count = 0;

    HRESULT hr = MFTEnumEx(
        MFT_CATEGORY_VIDEO_DECODER,
        MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
        &inputType,
        &outputType,
        &ppActivate,
        &count
    );

    if (FAILED(hr) || count == 0) {
        // Fall back to software decoder
        hr = MFTEnumEx(
            MFT_CATEGORY_VIDEO_DECODER,
            MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
            &inputType,
            &outputType,
            &ppActivate,
            &count
        );
    }

    if (FAILED(hr) || count == 0) return false;

    // 2. Create the decoder
    hr = ppActivate[0]->ActivateObject(IID_PPV_ARGS(&m_decoder));

    // Clean up activation objects
    for (UINT32 i = 0; i < count; i++) {
        ppActivate[i]->Release();
    }
    CoTaskMemFree(ppActivate);

    if (FAILED(hr)) return false;

    // 3. Set up DXGI device manager for hardware acceleration
    hr = MFCreateDXGIDeviceManager(&m_resetToken, &m_deviceManager);
    if (FAILED(hr)) return false;

    hr = m_deviceManager->ResetDevice(m_device, m_resetToken);
    if (FAILED(hr)) return false;

    // 4. Configure decoder to use D3D11
    hr = m_decoder->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER,
                                    (ULONG_PTR)m_deviceManager);
    // Note: This may fail for software decoders, which is OK

    // 5. Set input type (H264)
    IMFMediaType* inputMediaType = nullptr;
    MFCreateMediaType(&inputMediaType);
    inputMediaType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    inputMediaType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
    inputMediaType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    MFSetAttributeSize(inputMediaType, MF_MT_FRAME_SIZE, m_width, m_height);

    hr = m_decoder->SetInputType(0, inputMediaType, 0);
    inputMediaType->Release();
    if (FAILED(hr)) return false;

    // 6. Set output type (NV12)
    IMFMediaType* outputMediaType = nullptr;
    MFCreateMediaType(&outputMediaType);
    outputMediaType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    outputMediaType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    MFSetAttributeSize(outputMediaType, MF_MT_FRAME_SIZE, m_width, m_height);

    hr = m_decoder->SetOutputType(0, outputMediaType, 0);
    outputMediaType->Release();
    if (FAILED(hr)) return false;

    // 7. Start decoder
    hr = m_decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);

    return SUCCEEDED(hr);
}
```

### Creating Sample Buffer from NAL Unit

```cpp
IMFSample* MediaFoundationDecoder::CreateSampleFromNAL(
    const uint8_t* nalData, int nalLength, bool isKeyframe) {

    // For H264, we need to convert from Annex B (start codes) to
    // length-prefixed format if not already
    // WebRTC typically sends NAL units without start codes

    // Create buffer with 4-byte length prefix (big-endian)
    int totalLength = nalLength + 4;

    IMFMediaBuffer* buffer = nullptr;
    HRESULT hr = MFCreateMemoryBuffer(totalLength, &buffer);
    if (FAILED(hr)) return nullptr;

    BYTE* bufferData = nullptr;
    hr = buffer->Lock(&bufferData, nullptr, nullptr);
    if (FAILED(hr)) {
        buffer->Release();
        return nullptr;
    }

    // Write length prefix (big-endian)
    bufferData[0] = (nalLength >> 24) & 0xFF;
    bufferData[1] = (nalLength >> 16) & 0xFF;
    bufferData[2] = (nalLength >> 8) & 0xFF;
    bufferData[3] = nalLength & 0xFF;

    // Copy NAL data
    memcpy(bufferData + 4, nalData, nalLength);

    buffer->Unlock();
    buffer->SetCurrentLength(totalLength);

    // Create sample
    IMFSample* sample = nullptr;
    hr = MFCreateSample(&sample);
    if (FAILED(hr)) {
        buffer->Release();
        return nullptr;
    }

    sample->AddBuffer(buffer);
    buffer->Release();

    // Set sample time (can use 0 for real-time streaming)
    sample->SetSampleTime(0);
    sample->SetSampleDuration(0);

    if (isKeyframe) {
        sample->SetUINT32(MFSampleExtension_CleanPoint, TRUE);
    }

    return sample;
}
```

### Decode and Render

```cpp
bool MediaFoundationDecoder::DecodeAndRender(
    const uint8_t* nalData, int nalLength, bool isKeyframe) {

    if (!m_decoder) return false;

    // 1. Create input sample
    IMFSample* inputSample = CreateSampleFromNAL(nalData, nalLength, isKeyframe);
    if (!inputSample) return false;

    // 2. Feed to decoder
    HRESULT hr = m_decoder->ProcessInput(0, inputSample, 0);
    inputSample->Release();

    if (FAILED(hr) && hr != MF_E_NOTACCEPTING) {
        return false;
    }

    // 3. Try to get output
    MFT_OUTPUT_DATA_BUFFER outputBuffer = {};
    outputBuffer.dwStreamID = 0;
    outputBuffer.pSample = nullptr;
    outputBuffer.dwStatus = 0;
    outputBuffer.pEvents = nullptr;

    // Check if decoder allocates its own samples
    MFT_OUTPUT_STREAM_INFO streamInfo = {};
    m_decoder->GetOutputStreamInfo(0, &streamInfo);

    bool decoderAllocates = (streamInfo.dwFlags &
        (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;

    IMFSample* outputSample = nullptr;
    if (!decoderAllocates) {
        // We need to provide output sample
        MFCreateSample(&outputSample);
        IMFMediaBuffer* outBuffer = nullptr;
        MFCreateMemoryBuffer(m_width * m_height * 3 / 2, &outBuffer);
        outputSample->AddBuffer(outBuffer);
        outBuffer->Release();
        outputBuffer.pSample = outputSample;
    }

    DWORD status = 0;
    hr = m_decoder->ProcessOutput(0, 1, &outputBuffer, &status);

    if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) {
        // Decoder needs more data
        if (outputSample) outputSample->Release();
        return true;  // Not an error, just buffering
    }

    if (FAILED(hr)) {
        if (outputSample) outputSample->Release();
        return false;
    }

    // 4. Get the decoded frame and render it
    IMFSample* decodedSample = outputBuffer.pSample;
    if (decodedSample) {
        RenderDecodedFrame(decodedSample);
        decodedSample->Release();
    }

    return true;
}
```

### NV12 to RGB Pixel Shader

```hlsl
// NV12 to RGB conversion shader
// nv12_shader.hlsl

Texture2D<float> yTexture : register(t0);
Texture2D<float2> uvTexture : register(t1);
SamplerState samplerState : register(s0);

struct VS_OUTPUT {
    float4 position : SV_POSITION;
    float2 texCoord : TEXCOORD0;
};

float4 main(VS_OUTPUT input) : SV_TARGET {
    float y = yTexture.Sample(samplerState, input.texCoord);
    float2 uv = uvTexture.Sample(samplerState, input.texCoord);

    // BT.601 conversion (video range)
    y = (y - 0.0625) * 1.164;
    float u = uv.x - 0.5;
    float v = uv.y - 0.5;

    float r = y + 1.596 * v;
    float g = y - 0.391 * u - 0.813 * v;
    float b = y + 2.018 * u;

    return float4(saturate(r), saturate(g), saturate(b), 1.0);
}
```

### Overlay Window Approach

```cpp
// Creating overlay window (same pattern as macOS)
bool MediaFoundationDecoder::CreateOverlayWindow() {
    // Register window class
    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(WNDCLASSEXW);
    wc.style = CS_HREDRAW | CS_VREDRAW;
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandle(nullptr);
    wc.lpszClassName = L"MiscordVideoOverlay";
    RegisterClassExW(&wc);

    // Create borderless child window
    m_hwnd = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE,
        L"MiscordVideoOverlay",
        L"",
        WS_POPUP | WS_VISIBLE,
        0, 0, m_width, m_height,
        nullptr,  // Will be set as child later
        nullptr,
        GetModuleHandle(nullptr),
        nullptr
    );

    if (!m_hwnd) return false;

    // Make click-through (like macOS ignoresMouseEvents)
    SetLayeredWindowAttributes(m_hwnd, 0, 255, LWA_ALPHA);

    return true;
}

void MediaFoundationDecoder::AttachToParent(HWND parent) {
    m_parentHwnd = parent;
    SetParent(m_hwnd, parent);

    // Position overlay to match parent
    RECT rect;
    GetClientRect(parent, &rect);
    SetWindowPos(m_hwnd, HWND_TOP, 0, 0,
                 rect.right - rect.left,
                 rect.bottom - rect.top,
                 SWP_SHOWWINDOW);
}
```

## C# Integration

### P/Invoke Wrapper

See `MediaFoundationDecoder.cs` stub file in `src/Miscord.Client/Services/HardwareVideo/`

### Integration with Factory

Update `IHardwareVideoDecoder.cs`:

```csharp
public static IHardwareVideoDecoder? Create()
{
    if (OperatingSystem.IsMacOS())
    {
        return VideoToolboxDecoder.TryCreate();
    }

    if (OperatingSystem.IsWindows())
    {
        return MediaFoundationDecoder.TryCreate();  // Add this
    }

    if (OperatingSystem.IsLinux())
    {
        return VaapiDecoder.TryCreate();  // Add this
    }

    return null;
}
```

## Build Instructions

### Native Library

1. Create a Visual Studio C++ project (DLL)
2. Add the source files
3. Link against: `mfplat.lib`, `mfuuid.lib`, `d3d11.lib`, `dxgi.lib`
4. Build for x64 (and optionally ARM64 for Windows on ARM)
5. Copy `MiscordWindowsRenderer.dll` to the application output directory

### CMake Example

```cmake
cmake_minimum_required(VERSION 3.16)
project(MiscordWindowsRenderer)

set(CMAKE_CXX_STANDARD 17)

add_library(MiscordWindowsRenderer SHARED
    src/MediaFoundationDecoder.cpp
    src/D3D11Renderer.cpp
    src/CApi.cpp
)

target_link_libraries(MiscordWindowsRenderer PRIVATE
    mfplat
    mfuuid
    d3d11
    dxgi
)

# Compile shaders
add_custom_command(TARGET MiscordWindowsRenderer POST_BUILD
    COMMAND fxc /T ps_5_0 /E main /Fo ${CMAKE_BINARY_DIR}/nv12_ps.cso
            ${CMAKE_SOURCE_DIR}/shaders/nv12_shader.hlsl
)
```

## Testing Checklist

- [ ] `mf_decoder_is_available()` returns true on Windows 10+
- [ ] Decoder creates successfully
- [ ] SPS/PPS initialization works
- [ ] Keyframes decode correctly
- [ ] P-frames decode after keyframe
- [ ] Video displays in Avalonia NativeControlHost
- [ ] Overlay window positions correctly
- [ ] Window resizing works
- [ ] No memory leaks (check with Visual Studio diagnostics)
- [ ] Works with both hardware and software MFT fallback

## Common Issues and Solutions

### 1. MF_E_INVALIDMEDIATYPE
- Ensure SPS/PPS are valid H264 parameter sets
- Check width/height match the actual video

### 2. Black screen but no errors
- Check D3D11 device creation succeeded
- Verify swap chain format matches texture format
- Check viewport is set correctly

### 3. Overlay not visible
- Ensure parent HWND is valid
- Check z-order with SetWindowPos
- Verify WS_EX_LAYERED is set correctly

### 4. Poor performance
- Ensure hardware MFT is being used (not software fallback)
- Check DXGI device manager is properly configured
- Use async decode if supported

## References

- [Media Foundation H264 Decoder](https://docs.microsoft.com/en-us/windows/win32/medfound/h-264-video-decoder)
- [DXGI Device Manager](https://docs.microsoft.com/en-us/windows/win32/medfound/dxgi-device-manager)
- [D3D11 Video Processing](https://docs.microsoft.com/en-us/windows/win32/direct3d11/d3d11-graphics-reference-video)
