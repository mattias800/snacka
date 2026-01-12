#pragma once

#include <va/va.h>
#include <va/va_drm.h>
#include <va/va_enc_h264.h>

#include <functional>
#include <vector>
#include <atomic>
#include <cstdint>
#include <string>

namespace snacka {

/// Callback for encoded H.264 data
/// @param data Pointer to encoded NAL unit data (AVCC format with 4-byte length prefix)
/// @param size Size of the data
/// @param isKeyframe True if this is a keyframe (IDR)
using EncodedCallback = std::function<void(const uint8_t* data, size_t size, bool isKeyframe)>;

/// Hardware H.264 encoder using VAAPI.
/// Works with Intel, AMD, and some NVIDIA GPUs via mesa/nouveau.
/// Outputs H.264 NAL units in AVCC format (4-byte big-endian length prefix).
class VaapiEncoder {
public:
    VaapiEncoder(int width, int height, int fps, int bitrateMbps = 6);
    ~VaapiEncoder();

    /// Initialize the encoder
    /// @return true if initialization succeeded
    bool Initialize();

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
    const char* GetEncoderName() const { return m_encoderName.c_str(); }

    /// Check if the encoder is initialized
    bool IsInitialized() const { return m_initialized; }

private:
    bool OpenDrmDevice();
    bool CreateConfig();
    bool CreateSurfaces();
    bool CreateContext();
    bool CreateCodedBuffer();
    bool EncodeFrame(int64_t timestampMs, bool forceKeyframe);
    bool RenderPicture(VASurfaceID surface, bool isIdr);
    bool GetEncodedData(bool isKeyframe);
    void ConvertAnnexBToAVCC(const uint8_t* annexB, size_t size, bool isKeyframe);
    void Cleanup();

    // Configuration
    int m_width;
    int m_height;
    int m_fps;
    int m_bitrate;  // in bits per second
    int m_gopSize;  // Keyframe interval

    // State
    bool m_initialized = false;
    int64_t m_frameCount = 0;
    std::string m_encoderName = "VAAPI";

    // DRM and VAAPI objects
    int m_drmFd = -1;
    VADisplay m_vaDisplay = nullptr;
    VAConfigID m_configId = VA_INVALID_ID;
    VAContextID m_contextId = VA_INVALID_ID;
    VAProfile m_profile = VAProfileH264ConstrainedBaseline;

    // Surfaces for encoding (double buffered)
    static constexpr int NUM_SURFACES = 4;
    std::vector<VASurfaceID> m_surfaces;
    int m_currentSurface = 0;

    // Reference frame for P-frames
    VASurfaceID m_refSurface = VA_INVALID_SURFACE;
    int m_refSurfaceIndex = 0;

    // Coded buffer for output
    VABufferID m_codedBuf = VA_INVALID_ID;

    // Sequence and picture parameter buffers
    VABufferID m_seqParamBuf = VA_INVALID_ID;
    VABufferID m_picParamBuf = VA_INVALID_ID;
    VABufferID m_sliceParamBuf = VA_INVALID_ID;

    // SPS/PPS NAL units (stored after first keyframe)
    std::vector<uint8_t> m_sps;
    std::vector<uint8_t> m_pps;
    bool m_haveSpsPs = false;

    // Output buffers
    std::vector<uint8_t> m_avccBuffer;

    // Callback
    EncodedCallback m_callback;

    // Frame order tracking
    int m_frameNumInGop = 0;
    int m_idrPicId = 0;
};

}  // namespace snacka
