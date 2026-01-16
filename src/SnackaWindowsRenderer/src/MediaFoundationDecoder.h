#pragma once

#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <vector>
#include <memory>

// Forward declaration
class D3D11Renderer;

class MediaFoundationDecoder {
public:
    MediaFoundationDecoder();
    ~MediaFoundationDecoder();

    // Non-copyable
    MediaFoundationDecoder(const MediaFoundationDecoder&) = delete;
    MediaFoundationDecoder& operator=(const MediaFoundationDecoder&) = delete;

    // Initialize the decoder with video dimensions and H.264 SPS/PPS
    bool Initialize(int width, int height,
                    const uint8_t* sps, int spsLen,
                    const uint8_t* pps, int ppsLen);

    // Decode a NAL unit and render to the display
    bool DecodeAndRender(const uint8_t* nalData, int nalLen, bool isKeyframe);

    // Render raw NV12 data directly (without decoding)
    bool RenderNV12Frame(const uint8_t* nv12Data, int dataLen, int width, int height);

    // Get the window handle for embedding
    HWND GetView() const;

    // Set the display size
    void SetDisplaySize(int width, int height);

    // Check if Media Foundation H.264 decoding is available
    static bool IsAvailable();

    // Get decoder statistics
    int GetOutputCount() const { return m_outputCount; }
    int GetNeedInputCount() const { return m_needInputCount; }

private:
    // Create D3D11 device
    bool CreateD3D11Device();

    // Create the H.264 decoder MFT
    bool CreateDecoder();

    // Configure decoder input/output types
    bool ConfigureDecoder();

    // Create a sample from NAL unit data
    IMFSample* CreateSampleFromNAL(const uint8_t* nalData, int nalLen, bool isKeyframe);

    // Render a decoded frame
    void RenderFrame(IMFSample* sample);

    // Cleanup
    void Cleanup();

private:
    // D3D11
    ID3D11Device* m_device = nullptr;
    ID3D11DeviceContext* m_context = nullptr;
    IMFDXGIDeviceManager* m_deviceManager = nullptr;
    UINT m_resetToken = 0;

    // Media Foundation decoder
    IMFTransform* m_decoder = nullptr;

    // Renderer
    std::unique_ptr<D3D11Renderer> m_renderer;

    // Video parameters
    int m_width = 0;
    int m_height = 0;
    std::vector<uint8_t> m_sps;
    std::vector<uint8_t> m_pps;

    // State
    bool m_initialized = false;
    bool m_mfInitialized = false;

    // Statistics
    int m_outputCount = 0;
    int m_needInputCount = 0;
};
