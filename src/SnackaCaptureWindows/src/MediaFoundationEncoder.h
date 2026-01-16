#pragma once

#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <codecapi.h>
#include <strmif.h>    // For ICodecAPI
#include <d3d11.h>
#include <dxgi.h>
#include <wrl/client.h>

#include <functional>
#include <vector>
#include <atomic>
#include <cstdint>

namespace snacka {

using Microsoft::WRL::ComPtr;

/// Callback for encoded H.264 data
/// @param data Pointer to encoded NAL unit data (AVCC format with 4-byte length prefix)
/// @param size Size of the data
/// @param isKeyframe True if this is a keyframe (IDR)
using EncodedCallback = std::function<void(const uint8_t* data, size_t size, bool isKeyframe)>;

/// Hardware H.264 encoder using Media Foundation Transform (MFT).
/// Automatically uses NVENC (NVIDIA), AMF (AMD), or QuickSync (Intel) based on available hardware.
/// Outputs H.264 NAL units in AVCC format (4-byte big-endian length prefix).
class MediaFoundationEncoder {
public:
    MediaFoundationEncoder(int width, int height, int fps, int bitrateMbps = 6);
    ~MediaFoundationEncoder();

    /// Initialize the encoder with an optional D3D11 device
    /// @param device The D3D11 device to use for hardware encoding (optional, will create own if nullptr)
    /// @return true if initialization succeeded
    bool Initialize(ID3D11Device* device = nullptr);

    /// Encode a D3D11 texture
    /// @param texture The texture to encode (must be NV12 or will be converted)
    /// @param timestampMs Presentation timestamp in milliseconds
    /// @return true if the frame was submitted for encoding
    bool EncodeFrame(ID3D11Texture2D* texture, int64_t timestampMs);

    /// Encode raw NV12 data
    /// @param nv12Data Pointer to NV12 frame data
    /// @param size Size of the data
    /// @param timestampMs Presentation timestamp in milliseconds
    /// @return true if the frame was submitted for encoding
    bool EncodeNV12(const uint8_t* nv12Data, size_t size, int64_t timestampMs);

    /// Flush any pending frames
    void Flush();

    /// Stop the encoder and release resources
    void Stop();

    /// Set the callback for encoded data
    void SetCallback(EncodedCallback callback) { m_callback = callback; }

    /// Check if a hardware H.264 encoder is available on this system
    static bool IsHardwareEncoderAvailable();

    /// Get the name of the encoder being used
    const char* GetEncoderName() const { return m_encoderName; }

    /// Check if the encoder is initialized
    bool IsInitialized() const { return m_initialized; }

private:
    bool CreateEncoder();
    bool ConfigureEncoder();
    bool SetInputType();
    bool SetOutputType();
    bool ProcessOutput();
    bool RetrieveOutput();
    void OutputNalUnits(const uint8_t* data, size_t size, bool isKeyframe);

    // Configuration
    int m_width;
    int m_height;
    int m_fps;
    int m_bitrate;  // in bits per second

    // State
    bool m_initialized = false;
    bool m_isAsync = false;
    int64_t m_frameCount = 0;
    const char* m_encoderName = "Unknown";
    ComPtr<IMFMediaEventGenerator> m_eventGen;

    // Media Foundation objects
    ComPtr<IMFTransform> m_encoder;
    ComPtr<IMFDXGIDeviceManager> m_deviceManager;
    UINT m_resetToken = 0;

    // D3D11 resources
    ComPtr<ID3D11Device> m_device;
    ComPtr<ID3D11DeviceContext> m_context;
    ComPtr<ID3D11Texture2D> m_stagingTexture;  // For CPU write (STAGING)
    ComPtr<ID3D11Texture2D> m_gpuTexture;      // For GPU read (DEFAULT)

    // Output buffer
    std::vector<uint8_t> m_outputBuffer;

    // Callback
    EncodedCallback m_callback;

    // Stream IDs
    DWORD m_inputStreamId = 0;
    DWORD m_outputStreamId = 0;
};

}  // namespace snacka
