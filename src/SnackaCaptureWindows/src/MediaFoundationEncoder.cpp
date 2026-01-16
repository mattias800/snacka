#include "MediaFoundationEncoder.h"

#include <mfreadwrite.h>
#include <Mfobjects.h>
#include <iostream>
#include <cstdlib>  // For _byteswap_ulong

// Use Windows byte swap instead of winsock's htonl to avoid header conflicts
#define htonl(x) _byteswap_ulong(x)

#pragma comment(lib, "mf.lib")
#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "mfreadwrite.lib")

namespace snacka {

MediaFoundationEncoder::MediaFoundationEncoder(int width, int height, int fps, int bitrateMbps)
    : m_width(width)
    , m_height(height)
    , m_fps(fps)
    , m_bitrate(bitrateMbps * 1000000) {
}

MediaFoundationEncoder::~MediaFoundationEncoder() {
    Stop();
}

bool MediaFoundationEncoder::IsHardwareEncoderAvailable() {
    // Initialize MF
    HRESULT hr = MFStartup(MF_VERSION);
    if (FAILED(hr)) return false;

    // Look for hardware H.264 encoders
    MFT_REGISTER_TYPE_INFO inputType = { MFMediaType_Video, MFVideoFormat_NV12 };
    MFT_REGISTER_TYPE_INFO outputType = { MFMediaType_Video, MFVideoFormat_H264 };

    UINT32 flags = MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER;

    IMFActivate** activates = nullptr;
    UINT32 count = 0;

    hr = MFTEnumEx(
        MFT_CATEGORY_VIDEO_ENCODER,
        flags,
        &inputType,
        &outputType,
        &activates,
        &count
    );

    // Clean up
    if (activates) {
        for (UINT32 i = 0; i < count; i++) {
            activates[i]->Release();
        }
        CoTaskMemFree(activates);
    }

    MFShutdown();

    return SUCCEEDED(hr) && count > 0;
}

bool MediaFoundationEncoder::Initialize(ID3D11Device* device) {
    if (m_initialized) return true;

    // Use provided device or create our own
    if (device) {
        m_device = device;
        device->GetImmediateContext(&m_context);
    } else {
        // Create our own D3D11 device
        D3D_FEATURE_LEVEL featureLevel;
        UINT createFlags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;

        HRESULT hr = D3D11CreateDevice(
            nullptr,
            D3D_DRIVER_TYPE_HARDWARE,
            nullptr,
            createFlags,
            nullptr, 0,
            D3D11_SDK_VERSION,
            &m_device,
            &featureLevel,
            &m_context
        );

        if (FAILED(hr)) {
            std::cerr << "MediaFoundationEncoder: Failed to create D3D11 device: 0x"
                      << std::hex << hr << std::dec << "\n";
            return false;
        }
    }

    // Initialize Media Foundation
    HRESULT hr = MFStartup(MF_VERSION);
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Failed to initialize MF: 0x"
                  << std::hex << hr << std::dec << "\n";
        return false;
    }

    // Create DXGI Device Manager for hardware acceleration
    hr = MFCreateDXGIDeviceManager(&m_resetToken, &m_deviceManager);
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Failed to create DXGI device manager: 0x"
                  << std::hex << hr << std::dec << "\n";
        return false;
    }

    hr = m_deviceManager->ResetDevice(m_device.Get(), m_resetToken);
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Failed to reset device manager: 0x"
                  << std::hex << hr << std::dec << "\n";
        return false;
    }

    // Create and configure encoder
    if (!CreateEncoder()) {
        return false;
    }

    if (!ConfigureEncoder()) {
        return false;
    }

    if (!SetOutputType()) {
        return false;
    }

    if (!SetInputType()) {
        return false;
    }

    // Start processing
    hr = m_encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Failed to begin streaming: 0x"
                  << std::hex << hr << std::dec << "\n";
        return false;
    }

    hr = m_encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Failed to start stream: 0x"
                  << std::hex << hr << std::dec << "\n";
        return false;
    }

    // Create staging texture for NV12 upload (use STAGING for CPU write access)
    D3D11_TEXTURE2D_DESC stagingDesc = {};
    stagingDesc.Width = m_width;
    stagingDesc.Height = m_height;
    stagingDesc.MipLevels = 1;
    stagingDesc.ArraySize = 1;
    stagingDesc.Format = DXGI_FORMAT_NV12;
    stagingDesc.SampleDesc.Count = 1;
    stagingDesc.Usage = D3D11_USAGE_STAGING;
    stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    stagingDesc.BindFlags = 0;  // No bind flags for staging textures

    hr = m_device->CreateTexture2D(&stagingDesc, nullptr, &m_stagingTexture);
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Failed to create staging texture: 0x"
                  << std::hex << hr << std::dec << "\n";
        return false;
    }

    // Create GPU texture for encoder input (DEFAULT for GPU access)
    D3D11_TEXTURE2D_DESC gpuDesc = stagingDesc;
    gpuDesc.Usage = D3D11_USAGE_DEFAULT;
    gpuDesc.CPUAccessFlags = 0;
    gpuDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;

    hr = m_device->CreateTexture2D(&gpuDesc, nullptr, &m_gpuTexture);
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Failed to create GPU texture: 0x"
                  << std::hex << hr << std::dec << "\n";
        return false;
    }

    m_initialized = true;
    std::cerr << "MediaFoundationEncoder: Initialized (" << m_encoderName << ") "
              << m_width << "x" << m_height << " @ " << m_fps << "fps, "
              << (m_bitrate / 1000000) << "Mbps\n";

    return true;
}

bool MediaFoundationEncoder::CreateEncoder() {
    // Find hardware H.264 encoders
    MFT_REGISTER_TYPE_INFO inputType = { MFMediaType_Video, MFVideoFormat_NV12 };
    MFT_REGISTER_TYPE_INFO outputType = { MFMediaType_Video, MFVideoFormat_H264 };

    // Try hardware first
    UINT32 flags = MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER;

    IMFActivate** activates = nullptr;
    UINT32 count = 0;

    HRESULT hr = MFTEnumEx(
        MFT_CATEGORY_VIDEO_ENCODER,
        flags,
        &inputType,
        &outputType,
        &activates,
        &count
    );

    if (FAILED(hr) || count == 0) {
        // Try any encoder (including software)
        flags = MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER;
        hr = MFTEnumEx(
            MFT_CATEGORY_VIDEO_ENCODER,
            flags,
            &inputType,
            &outputType,
            &activates,
            &count
        );

        if (FAILED(hr) || count == 0) {
            std::cerr << "MediaFoundationEncoder: No H.264 encoder found\n";
            return false;
        }
        m_encoderName = "Software";
    } else {
        // Get encoder name
        WCHAR* friendlyName = nullptr;
        UINT32 nameLen = 0;
        if (SUCCEEDED(activates[0]->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, &friendlyName, &nameLen))) {
            // Convert to narrow string for logging
            if (wcsstr(friendlyName, L"NVIDIA") || wcsstr(friendlyName, L"nvenc")) {
                m_encoderName = "NVIDIA NVENC";
            } else if (wcsstr(friendlyName, L"AMD") || wcsstr(friendlyName, L"AMF")) {
                m_encoderName = "AMD AMF";
            } else if (wcsstr(friendlyName, L"Intel") || wcsstr(friendlyName, L"Quick Sync")) {
                m_encoderName = "Intel QuickSync";
            } else {
                m_encoderName = "Hardware";
            }
            CoTaskMemFree(friendlyName);
        } else {
            m_encoderName = "Hardware";
        }
    }

    // Activate the first encoder
    hr = activates[0]->ActivateObject(IID_PPV_ARGS(&m_encoder));

    // Clean up activates
    for (UINT32 i = 0; i < count; i++) {
        activates[i]->Release();
    }
    CoTaskMemFree(activates);

    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Failed to activate encoder: 0x"
                  << std::hex << hr << std::dec << "\n";
        return false;
    }

    // Check if this is an async MFT and unlock it
    ComPtr<IMFAttributes> pAttributes;
    hr = m_encoder->GetAttributes(&pAttributes);
    if (SUCCEEDED(hr) && pAttributes) {
        UINT32 isAsync = 0;
        hr = pAttributes->GetUINT32(MF_TRANSFORM_ASYNC, &isAsync);
        if (SUCCEEDED(hr) && isAsync) {
            m_isAsync = true;
            std::cerr << "MediaFoundationEncoder: Async MFT detected, unlocking...\n";
            hr = pAttributes->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE);
            if (FAILED(hr)) {
                std::cerr << "MediaFoundationEncoder: Warning - Failed to unlock async MFT: 0x"
                          << std::hex << hr << std::dec << "\n";
            }

            // Get event generator for async MFT
            hr = m_encoder->QueryInterface(IID_PPV_ARGS(&m_eventGen));
            if (FAILED(hr)) {
                std::cerr << "MediaFoundationEncoder: Warning - Failed to get event generator: 0x"
                          << std::hex << hr << std::dec << "\n";
                m_eventGen = nullptr;
            }
        }
    }

    // Get stream IDs
    DWORD inputCount = 0, outputCount = 0;
    hr = m_encoder->GetStreamCount(&inputCount, &outputCount);
    if (SUCCEEDED(hr) && inputCount > 0 && outputCount > 0) {
        DWORD* inputIds = new DWORD[inputCount];
        DWORD* outputIds = new DWORD[outputCount];
        hr = m_encoder->GetStreamIDs(inputCount, inputIds, outputCount, outputIds);
        if (SUCCEEDED(hr)) {
            m_inputStreamId = inputIds[0];
            m_outputStreamId = outputIds[0];
        } else if (hr == E_NOTIMPL) {
            // Streams are numbered sequentially starting at 0
            m_inputStreamId = 0;
            m_outputStreamId = 0;
        }
        delete[] inputIds;
        delete[] outputIds;
    }

    // Set DXGI device manager for hardware acceleration
    hr = m_encoder->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER,
                                    reinterpret_cast<ULONG_PTR>(m_deviceManager.Get()));
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Warning - Failed to set D3D manager: 0x"
                  << std::hex << hr << std::dec << " (continuing without GPU acceleration)\n";
    }

    return true;
}

bool MediaFoundationEncoder::ConfigureEncoder() {
    // Get codec API interface
    ComPtr<ICodecAPI> codecApi;
    HRESULT hr = m_encoder->QueryInterface(IID_PPV_ARGS(&codecApi));
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Warning - No codec API available\n";
        return true;  // Continue without codec API configuration
    }

    VARIANT var;

    // Low latency mode - critical for real-time streaming
    VariantInit(&var);
    var.vt = VT_BOOL;
    var.boolVal = VARIANT_TRUE;
    hr = codecApi->SetValue(&CODECAPI_AVLowLatencyMode, &var);
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Warning - Failed to set low latency mode\n";
    }

    // CBR rate control for consistent bitrate
    VariantInit(&var);
    var.vt = VT_UI4;
    var.ulVal = eAVEncCommonRateControlMode_CBR;
    hr = codecApi->SetValue(&CODECAPI_AVEncCommonRateControlMode, &var);
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Warning - Failed to set rate control mode\n";
    }

    // Average bitrate
    VariantInit(&var);
    var.vt = VT_UI4;
    var.ulVal = m_bitrate;
    hr = codecApi->SetValue(&CODECAPI_AVEncCommonMeanBitRate, &var);
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Warning - Failed to set bitrate\n";
    }

    // GOP size (keyframe interval) - one per second
    VariantInit(&var);
    var.vt = VT_UI4;
    var.ulVal = m_fps;
    hr = codecApi->SetValue(&CODECAPI_AVEncMPVGOPSize, &var);
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Warning - Failed to set GOP size\n";
    }

    // Disable B-frames for lower latency
    VariantInit(&var);
    var.vt = VT_UI4;
    var.ulVal = 0;
    codecApi->SetValue(&CODECAPI_AVEncMPVDefaultBPictureCount, &var);

    return true;
}

bool MediaFoundationEncoder::SetOutputType() {
    ComPtr<IMFMediaType> outputType;
    HRESULT hr = MFCreateMediaType(&outputType);
    if (FAILED(hr)) return false;

    hr = outputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (FAILED(hr)) return false;

    hr = outputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
    if (FAILED(hr)) return false;

    hr = MFSetAttributeSize(outputType.Get(), MF_MT_FRAME_SIZE, m_width, m_height);
    if (FAILED(hr)) return false;

    hr = MFSetAttributeRatio(outputType.Get(), MF_MT_FRAME_RATE, m_fps, 1);
    if (FAILED(hr)) return false;

    hr = outputType->SetUINT32(MF_MT_AVG_BITRATE, m_bitrate);
    if (FAILED(hr)) return false;

    hr = outputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (FAILED(hr)) return false;

    // Add pixel aspect ratio (1:1 for square pixels)
    hr = MFSetAttributeRatio(outputType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Warning - Failed to set output pixel aspect ratio\n";
    }

    // H.264 Baseline profile for maximum compatibility and no B-frames
    hr = outputType->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Base);
    if (FAILED(hr)) {
        // Try without profile (some encoders don't support it)
        std::cerr << "MediaFoundationEncoder: Warning - Failed to set H.264 profile\n";
    }

    // H.264 Level 4.1 (supports up to 1080p@30fps or 720p@60fps)
    hr = outputType->SetUINT32(MF_MT_MPEG2_LEVEL, eAVEncH264VLevel4_1);
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Warning - Failed to set H.264 level\n";
    }

    hr = m_encoder->SetOutputType(m_outputStreamId, outputType.Get(), 0);
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Failed to set output type: 0x"
                  << std::hex << hr << std::dec << "\n";
        return false;
    }

    return true;
}

bool MediaFoundationEncoder::SetInputType() {
    ComPtr<IMFMediaType> inputType;
    HRESULT hr = MFCreateMediaType(&inputType);
    if (FAILED(hr)) return false;

    hr = inputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (FAILED(hr)) return false;

    hr = inputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    if (FAILED(hr)) return false;

    hr = MFSetAttributeSize(inputType.Get(), MF_MT_FRAME_SIZE, m_width, m_height);
    if (FAILED(hr)) return false;

    hr = MFSetAttributeRatio(inputType.Get(), MF_MT_FRAME_RATE, m_fps, 1);
    if (FAILED(hr)) return false;

    hr = inputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (FAILED(hr)) return false;

    // Add pixel aspect ratio (1:1 for square pixels)
    hr = MFSetAttributeRatio(inputType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Warning - Failed to set pixel aspect ratio\n";
    }

    // Add default stride for NV12 (Y plane stride = width)
    hr = inputType->SetUINT32(MF_MT_DEFAULT_STRIDE, m_width);
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Warning - Failed to set default stride\n";
    }

    // Add sample size for NV12 (Y plane + UV plane = width * height * 1.5)
    UINT32 sampleSize = static_cast<UINT32>(m_width * m_height * 3 / 2);
    hr = inputType->SetUINT32(MF_MT_SAMPLE_SIZE, sampleSize);
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Warning - Failed to set sample size\n";
    }

    hr = m_encoder->SetInputType(m_inputStreamId, inputType.Get(), 0);
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Failed to set input type: 0x"
                  << std::hex << hr << std::dec << "\n";
        return false;
    }

    return true;
}

bool MediaFoundationEncoder::EncodeFrame(ID3D11Texture2D* texture, int64_t timestampMs) {
    if (!m_initialized) return false;

    // Create MF sample from D3D11 texture
    ComPtr<IMFMediaBuffer> buffer;
    HRESULT hr = MFCreateDXGISurfaceBuffer(
        __uuidof(ID3D11Texture2D),
        texture,
        0,
        FALSE,
        &buffer
    );

    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Failed to create DXGI buffer: 0x"
                  << std::hex << hr << std::dec << "\n";
        return false;
    }

    ComPtr<IMFSample> sample;
    hr = MFCreateSample(&sample);
    if (FAILED(hr)) return false;

    hr = sample->AddBuffer(buffer.Get());
    if (FAILED(hr)) return false;

    // Set timestamp (100ns units)
    hr = sample->SetSampleTime(timestampMs * 10000);
    if (FAILED(hr)) return false;

    // Set duration
    hr = sample->SetSampleDuration(10000000LL / m_fps);
    if (FAILED(hr)) return false;

    // Process input
    hr = m_encoder->ProcessInput(m_inputStreamId, sample.Get(), 0);
    if (FAILED(hr)) {
        if (hr == MF_E_NOTACCEPTING) {
            // Need to drain output first
            ProcessOutput();
            hr = m_encoder->ProcessInput(m_inputStreamId, sample.Get(), 0);
        }
        if (FAILED(hr)) {
            std::cerr << "MediaFoundationEncoder: ProcessInput failed: 0x"
                      << std::hex << hr << std::dec << "\n";
            return false;
        }
    }

    m_frameCount++;

    // Try to get output
    ProcessOutput();

    return true;
}

bool MediaFoundationEncoder::EncodeNV12(const uint8_t* nv12Data, size_t size, int64_t timestampMs) {
    if (!m_initialized) return false;

    // Upload to staging texture (CPU accessible)
    D3D11_MAPPED_SUBRESOURCE mapped;
    HRESULT hr = m_context->Map(m_stagingTexture.Get(), 0, D3D11_MAP_WRITE, 0, &mapped);
    if (FAILED(hr)) {
        std::cerr << "MediaFoundationEncoder: Map failed: 0x" << std::hex << hr << std::dec << "\n";
        return false;
    }

    // Copy Y plane
    const uint8_t* srcY = nv12Data;
    uint8_t* dstY = static_cast<uint8_t*>(mapped.pData);
    for (int y = 0; y < m_height; y++) {
        memcpy(dstY + y * mapped.RowPitch, srcY + y * m_width, m_width);
    }

    // Copy UV plane (interleaved, at half height)
    const uint8_t* srcUV = nv12Data + m_width * m_height;
    uint8_t* dstUV = dstY + mapped.RowPitch * m_height;
    for (int y = 0; y < m_height / 2; y++) {
        memcpy(dstUV + y * mapped.RowPitch, srcUV + y * m_width, m_width);
    }

    m_context->Unmap(m_stagingTexture.Get(), 0);

    // Copy from staging texture to GPU texture
    m_context->CopyResource(m_gpuTexture.Get(), m_stagingTexture.Get());

    // Encode the GPU texture
    return EncodeFrame(m_gpuTexture.Get(), timestampMs);
}

bool MediaFoundationEncoder::ProcessOutput() {
    if (!m_encoder) return false;

    // For async MFTs, we need to wait for events
    if (m_isAsync && m_eventGen) {
        // Try to get output events (non-blocking)
        while (true) {
            ComPtr<IMFMediaEvent> pEvent;
            HRESULT hr = m_eventGen->GetEvent(MF_EVENT_FLAG_NO_WAIT, &pEvent);

            if (hr == MF_E_NO_EVENTS_AVAILABLE) {
                // No more events, done for now
                return true;
            }

            if (FAILED(hr)) {
                return true;  // No events available
            }

            MediaEventType eventType;
            hr = pEvent->GetType(&eventType);
            if (FAILED(hr)) continue;

            if (eventType == METransformHaveOutput) {
                // Output is available, retrieve it
                if (!RetrieveOutput()) {
                    return false;
                }
            }
            else if (eventType == METransformNeedInput) {
                // Ready for more input - this is just informational for us
                return true;
            }
            else if (eventType == MEError) {
                HRESULT hrStatus;
                pEvent->GetStatus(&hrStatus);
                std::cerr << "MediaFoundationEncoder: MFT error event: 0x"
                          << std::hex << hrStatus << std::dec << "\n";
                return false;
            }
        }
    }

    // Synchronous MFT - use regular ProcessOutput loop
    while (true) {
        if (!RetrieveOutput()) {
            return true;  // No more output or error
        }
    }

    return true;
}

bool MediaFoundationEncoder::RetrieveOutput() {
    DWORD status = 0;
    MFT_OUTPUT_DATA_BUFFER outputBuffer = {};

    // Check if we need to provide output sample
    MFT_OUTPUT_STREAM_INFO streamInfo = {};
    HRESULT hr = m_encoder->GetOutputStreamInfo(m_outputStreamId, &streamInfo);
    if (FAILED(hr)) return false;

    ComPtr<IMFSample> outputSample;
    ComPtr<IMFMediaBuffer> outputMediaBuffer;

    if (!(streamInfo.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES)) {
        // We need to allocate the output sample
        hr = MFCreateSample(&outputSample);
        if (FAILED(hr)) return false;

        hr = MFCreateMemoryBuffer(streamInfo.cbSize ? streamInfo.cbSize : 1024 * 1024, &outputMediaBuffer);
        if (FAILED(hr)) return false;

        hr = outputSample->AddBuffer(outputMediaBuffer.Get());
        if (FAILED(hr)) return false;

        outputBuffer.pSample = outputSample.Get();
    }

    outputBuffer.dwStreamID = m_outputStreamId;

    hr = m_encoder->ProcessOutput(0, 1, &outputBuffer, &status);

    if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) {
        // No output available yet
        return false;
    }

    if (FAILED(hr)) {
        if (hr != E_UNEXPECTED) {  // E_UNEXPECTED is common for async MFTs when no output ready
            std::cerr << "MediaFoundationEncoder: ProcessOutput failed: 0x"
                      << std::hex << hr << std::dec << "\n";
        }
        return false;
    }

    // Get the output sample (either from our buffer or provided by encoder)
    IMFSample* sample = outputBuffer.pSample;
    if (!sample) return false;

    // Get buffer
    ComPtr<IMFMediaBuffer> buffer;
    hr = sample->GetBufferByIndex(0, &buffer);
    if (FAILED(hr)) return false;

    BYTE* data = nullptr;
    DWORD length = 0;
    hr = buffer->Lock(&data, nullptr, &length);
    if (FAILED(hr)) return false;

    // Check if keyframe
    UINT32 isKeyframe = 0;
    sample->GetUINT32(MFSampleExtension_CleanPoint, &isKeyframe);

    // Output NAL units in AVCC format
    OutputNalUnits(data, length, isKeyframe != 0);

    buffer->Unlock();

    if (outputBuffer.pEvents) {
        outputBuffer.pEvents->Release();
    }

    return true;
}

void MediaFoundationEncoder::OutputNalUnits(const uint8_t* data, size_t size, bool isKeyframe) {
    if (!m_callback || size == 0) return;

    // MFT outputs H.264 in Annex-B format (00 00 00 01 or 00 00 01 separators)
    // We need to convert to AVCC format (4-byte big-endian length prefix)

    m_outputBuffer.clear();

    size_t pos = 0;
    while (pos < size) {
        // Find start code (00 00 00 01 or 00 00 01)
        size_t startCodeLen = 0;
        size_t nalStart = pos;

        if (pos + 4 <= size && data[pos] == 0 && data[pos+1] == 0 && data[pos+2] == 0 && data[pos+3] == 1) {
            startCodeLen = 4;
            nalStart = pos + 4;
        } else if (pos + 3 <= size && data[pos] == 0 && data[pos+1] == 0 && data[pos+2] == 1) {
            startCodeLen = 3;
            nalStart = pos + 3;
        } else {
            pos++;
            continue;
        }

        // Find next start code or end of data
        size_t nalEnd = size;
        for (size_t i = nalStart; i + 3 <= size; i++) {
            if ((data[i] == 0 && data[i+1] == 0 && data[i+2] == 0 && data[i+3] == 1) ||
                (data[i] == 0 && data[i+1] == 0 && data[i+2] == 1)) {
                nalEnd = i;
                break;
            }
        }

        // Output NAL unit with 4-byte length prefix
        size_t nalLen = nalEnd - nalStart;
        if (nalLen > 0) {
            uint32_t lenBE = htonl(static_cast<uint32_t>(nalLen));
            m_outputBuffer.insert(m_outputBuffer.end(),
                                  reinterpret_cast<uint8_t*>(&lenBE),
                                  reinterpret_cast<uint8_t*>(&lenBE) + 4);
            m_outputBuffer.insert(m_outputBuffer.end(),
                                  data + nalStart,
                                  data + nalEnd);
        }

        pos = nalEnd;
    }

    if (!m_outputBuffer.empty()) {
        m_callback(m_outputBuffer.data(), m_outputBuffer.size(), isKeyframe);
    }
}

void MediaFoundationEncoder::Flush() {
    if (!m_encoder) return;

    // Send drain command
    m_encoder->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0);

    // Process remaining output
    while (ProcessOutput()) {
        // Keep processing until no more output
    }
}

void MediaFoundationEncoder::Stop() {
    if (m_encoder) {
        Flush();
        m_encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        m_encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
    }

    m_encoder.Reset();
    m_deviceManager.Reset();
    m_stagingTexture.Reset();
    m_gpuTexture.Reset();
    m_context.Reset();
    m_device.Reset();

    if (m_initialized) {
        MFShutdown();
        m_initialized = false;
        std::cerr << "MediaFoundationEncoder: Stopped after " << m_frameCount << " frames\n";
    }
}

}  // namespace snacka
