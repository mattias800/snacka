#pragma once

#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifdef SNACKA_RENDERER_EXPORTS
#define SNACKA_API __declspec(dllexport)
#else
#define SNACKA_API __declspec(dllimport)
#endif

// Opaque handle to decoder instance
typedef void* MFDecoderHandle;

// Create a new decoder instance
// Returns: Handle to decoder, or NULL on failure
SNACKA_API MFDecoderHandle mf_decoder_create();

// Destroy a decoder instance
SNACKA_API void mf_decoder_destroy(MFDecoderHandle decoder);

// Initialize decoder with video parameters
// spsData/ppsData: H264 parameter sets (without Annex B start codes)
// Returns: true on success
SNACKA_API bool mf_decoder_initialize(
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
SNACKA_API bool mf_decoder_decode_and_render(
    MFDecoderHandle decoder,
    const uint8_t* nalData,
    int nalLength,
    bool isKeyframe
);

// Get the native window handle (HWND) for embedding
// Returns: HWND that can be used with Avalonia's NativeControlHost
SNACKA_API void* mf_decoder_get_view(MFDecoderHandle decoder);

// Set the display size (for the renderer window)
SNACKA_API void mf_decoder_set_display_size(
    MFDecoderHandle decoder,
    int width,
    int height
);

// Check if Media Foundation H264 decoding is available
SNACKA_API bool mf_decoder_is_available();

// Reparent the video window to a new parent HWND
// This should be called after embedding in Avalonia NativeControlHost
SNACKA_API void mf_decoder_set_parent(MFDecoderHandle decoder, void* parentHwnd);

// Get decoder statistics for debugging
// Returns: number of frames that produced output (rendered)
SNACKA_API int mf_decoder_get_output_count(MFDecoderHandle decoder);

// Get count of frames that needed more input (didn't produce output)
SNACKA_API int mf_decoder_get_need_input_count(MFDecoderHandle decoder);

// Render raw NV12 frame data directly (without decoding)
// Use this when the capture tool outputs raw NV12 instead of H.264
SNACKA_API bool mf_decoder_render_nv12_frame(
    MFDecoderHandle decoder,
    const uint8_t* nv12Data,
    int dataLength,
    int width,
    int height
);

#ifdef __cplusplus
}
#endif
