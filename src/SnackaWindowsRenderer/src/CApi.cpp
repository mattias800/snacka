#include "CApi.h"
#include "MediaFoundationDecoder.h"
#include <unordered_map>
#include <mutex>
#include <iostream>
#include <sstream>
#include <Windows.h>

// Instance management
static std::unordered_map<MFDecoderHandle, MediaFoundationDecoder*> s_instances;
static std::mutex s_mutex;

extern "C" {

SNACKA_API MFDecoderHandle mf_decoder_create() {
    try {
        auto* decoder = new MediaFoundationDecoder();

        std::lock_guard<std::mutex> lock(s_mutex);
        s_instances[decoder] = decoder;

        return decoder;
    } catch (...) {
        return nullptr;
    }
}

SNACKA_API void mf_decoder_destroy(MFDecoderHandle handle) {
    if (!handle) return;

    MediaFoundationDecoder* decoder = nullptr;
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        auto it = s_instances.find(handle);
        if (it != s_instances.end()) {
            decoder = it->second;
            s_instances.erase(it);
        }
    }

    delete decoder;
}

SNACKA_API bool mf_decoder_initialize(
    MFDecoderHandle handle,
    int width,
    int height,
    const uint8_t* spsData,
    int spsLength,
    const uint8_t* ppsData,
    int ppsLength
) {
    if (!handle) return false;

    std::lock_guard<std::mutex> lock(s_mutex);
    auto it = s_instances.find(handle);
    if (it == s_instances.end()) return false;

    return it->second->Initialize(width, height, spsData, spsLength, ppsData, ppsLength);
}

static void DebugLog(const char* msg) {
    OutputDebugStringA(msg);
    OutputDebugStringA("\n");
    std::cerr << msg << std::endl;
    std::cerr.flush();
}

SNACKA_API bool mf_decoder_decode_and_render(
    MFDecoderHandle handle,
    const uint8_t* nalData,
    int nalLength,
    bool isKeyframe
) {
    static int apiCallCount = 0;
    apiCallCount++;
    if (apiCallCount <= 5 || apiCallCount % 100 == 0) {
        std::ostringstream oss;
        oss << "CApi::mf_decoder_decode_and_render: call " << apiCallCount
            << ", handle=" << handle << ", len=" << nalLength;
        DebugLog(oss.str().c_str());
    }

    if (!handle) {
        DebugLog("CApi::mf_decoder_decode_and_render: null handle!");
        return false;
    }

    std::lock_guard<std::mutex> lock(s_mutex);
    auto it = s_instances.find(handle);
    if (it == s_instances.end()) {
        DebugLog("CApi::mf_decoder_decode_and_render: handle not found!");
        return false;
    }

    bool result = it->second->DecodeAndRender(nalData, nalLength, isKeyframe);
    if (apiCallCount <= 5 || apiCallCount % 100 == 0) {
        std::ostringstream oss;
        oss << "CApi::mf_decoder_decode_and_render: call " << apiCallCount << " returned " << result;
        DebugLog(oss.str().c_str());
    }
    return result;
}

SNACKA_API void* mf_decoder_get_view(MFDecoderHandle handle) {
    if (!handle) return nullptr;

    std::lock_guard<std::mutex> lock(s_mutex);
    auto it = s_instances.find(handle);
    if (it == s_instances.end()) return nullptr;

    return it->second->GetView();
}

SNACKA_API void mf_decoder_set_display_size(
    MFDecoderHandle handle,
    int width,
    int height
) {
    if (!handle) return;

    std::lock_guard<std::mutex> lock(s_mutex);
    auto it = s_instances.find(handle);
    if (it == s_instances.end()) return;

    it->second->SetDisplaySize(width, height);
}

SNACKA_API bool mf_decoder_is_available() {
    return MediaFoundationDecoder::IsAvailable();
}

SNACKA_API int mf_decoder_get_output_count(MFDecoderHandle handle) {
    if (!handle) return 0;

    std::lock_guard<std::mutex> lock(s_mutex);
    auto it = s_instances.find(handle);
    if (it == s_instances.end()) return 0;

    return it->second->GetOutputCount();
}

SNACKA_API int mf_decoder_get_need_input_count(MFDecoderHandle handle) {
    if (!handle) return 0;

    std::lock_guard<std::mutex> lock(s_mutex);
    auto it = s_instances.find(handle);
    if (it == s_instances.end()) return 0;

    return it->second->GetNeedInputCount();
}

SNACKA_API bool mf_decoder_render_nv12_frame(
    MFDecoderHandle handle,
    const uint8_t* nv12Data,
    int dataLength,
    int width,
    int height
) {
    if (!handle) return false;

    std::lock_guard<std::mutex> lock(s_mutex);
    auto it = s_instances.find(handle);
    if (it == s_instances.end()) return false;

    return it->second->RenderNV12Frame(nv12Data, dataLength, width, height);
}

SNACKA_API bool mf_decoder_recreate_swap_chain(MFDecoderHandle handle) {
    if (!handle) return false;

    std::lock_guard<std::mutex> lock(s_mutex);
    auto it = s_instances.find(handle);
    if (it == s_instances.end()) return false;

    return it->second->RecreateSwapChain();
}

SNACKA_API bool mf_decoder_create_renderer_with_parent(MFDecoderHandle handle, void* parentHwnd) {
    if (!handle) return false;

    std::lock_guard<std::mutex> lock(s_mutex);
    auto it = s_instances.find(handle);
    if (it == s_instances.end()) return false;

    return it->second->CreateRendererWithParent(static_cast<HWND>(parentHwnd));
}

} // extern "C"
