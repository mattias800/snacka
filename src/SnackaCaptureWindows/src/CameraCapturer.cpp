#include "CameraCapturer.h"

#include <mferror.h>
#include <iostream>
#include <algorithm>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mf.lib")
#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "mfuuid.lib")

namespace snacka {

// Helper to convert wide string to UTF-8
static std::string WideToUtf8(const std::wstring& wide) {
    if (wide.empty()) return "";
    int size = WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), static_cast<int>(wide.size()), nullptr, 0, nullptr, nullptr);
    std::string result(size, 0);
    WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), static_cast<int>(wide.size()), result.data(), size, nullptr, nullptr);
    return result;
}

// Helper to convert UTF-8 to wide string
static std::wstring Utf8ToWide(const std::string& utf8) {
    if (utf8.empty()) return L"";
    int size = MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), static_cast<int>(utf8.size()), nullptr, 0);
    std::wstring result(size, 0);
    MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), static_cast<int>(utf8.size()), result.data(), size);
    return result;
}

CameraCapturer::CameraCapturer() {
    QueryPerformanceFrequency(&m_frequency);
}

CameraCapturer::~CameraCapturer() {
    Stop();

    if (m_outputType) {
        m_outputType->Release();
        m_outputType = nullptr;
    }

    if (m_sourceReader) {
        m_sourceReader->Release();
        m_sourceReader = nullptr;
    }
}

bool CameraCapturer::Initialize(const std::string& cameraId, int width, int height, int fps) {
    m_cameraId = cameraId;
    m_requestedWidth = width;
    m_requestedHeight = height;
    m_requestedFps = fps;

    // Initialize Media Foundation
    HRESULT hr = MFStartup(MF_VERSION);
    if (FAILED(hr)) {
        std::cerr << "CameraCapturer: Failed to initialize Media Foundation\n";
        return false;
    }

    // Create source reader
    if (!CreateSourceReader(cameraId)) {
        std::cerr << "CameraCapturer: Failed to create source reader\n";
        MFShutdown();
        return false;
    }

    // Configure media type for NV12 output
    if (!ConfigureMediaType()) {
        std::cerr << "CameraCapturer: Failed to configure media type\n";
        m_sourceReader->Release();
        m_sourceReader = nullptr;
        MFShutdown();
        return false;
    }

    std::cerr << "CameraCapturer: Initialized " << m_width << "x" << m_height
              << " @ " << m_requestedFps << "fps"
              << (m_isNV12Native ? " (native NV12)" : " (converting to NV12)") << "\n";

    return true;
}

bool CameraCapturer::CreateSourceReader(const std::string& cameraId) {
    HRESULT hr;

    // Enumerate devices to find the one matching cameraId
    IMFAttributes* pAttributes = nullptr;
    hr = MFCreateAttributes(&pAttributes, 1);
    if (FAILED(hr)) return false;

    hr = pAttributes->SetGUID(
        MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
        MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID
    );
    if (FAILED(hr)) {
        pAttributes->Release();
        return false;
    }

    IMFActivate** ppDevices = nullptr;
    UINT32 count = 0;
    hr = MFEnumDeviceSources(pAttributes, &ppDevices, &count);
    pAttributes->Release();

    if (FAILED(hr) || count == 0) {
        std::cerr << "CameraCapturer: No camera devices found\n";
        return false;
    }

    // Find the device matching cameraId (by symbolic link or index)
    IMFActivate* selectedDevice = nullptr;
    int requestedIndex = -1;

    // Try to parse as index first
    try {
        requestedIndex = std::stoi(cameraId);
    } catch (...) {
        requestedIndex = -1;
    }

    for (UINT32 i = 0; i < count; i++) {
        // Check if index matches
        if (requestedIndex >= 0 && static_cast<int>(i) == requestedIndex) {
            selectedDevice = ppDevices[i];
            selectedDevice->AddRef();
            break;
        }

        // Check if symbolic link matches
        WCHAR* symbolicLink = nullptr;
        UINT32 linkLength = 0;
        hr = ppDevices[i]->GetAllocatedString(
            MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK,
            &symbolicLink,
            &linkLength
        );
        if (SUCCEEDED(hr) && symbolicLink) {
            std::string link = WideToUtf8(symbolicLink);
            CoTaskMemFree(symbolicLink);

            if (link == cameraId) {
                selectedDevice = ppDevices[i];
                selectedDevice->AddRef();
                break;
            }
        }
    }

    // Default to first device if not found
    if (!selectedDevice && count > 0) {
        std::cerr << "CameraCapturer: Camera '" << cameraId << "' not found, using first available\n";
        selectedDevice = ppDevices[0];
        selectedDevice->AddRef();
    }

    // Clean up enumeration
    for (UINT32 i = 0; i < count; i++) {
        ppDevices[i]->Release();
    }
    CoTaskMemFree(ppDevices);

    if (!selectedDevice) {
        std::cerr << "CameraCapturer: No camera device available\n";
        return false;
    }

    // Create media source
    IMFMediaSource* pSource = nullptr;
    hr = selectedDevice->ActivateObject(IID_PPV_ARGS(&pSource));
    selectedDevice->Release();

    if (FAILED(hr)) {
        std::cerr << "CameraCapturer: Failed to activate camera device\n";
        return false;
    }

    // Create source reader
    IMFAttributes* pReaderAttributes = nullptr;
    hr = MFCreateAttributes(&pReaderAttributes, 1);
    if (SUCCEEDED(hr)) {
        // Request hardware transforms if available
        pReaderAttributes->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE);
    }

    hr = MFCreateSourceReaderFromMediaSource(pSource, pReaderAttributes, &m_sourceReader);

    if (pReaderAttributes) pReaderAttributes->Release();
    pSource->Release();

    if (FAILED(hr)) {
        std::cerr << "CameraCapturer: Failed to create source reader\n";
        return false;
    }

    return true;
}

bool CameraCapturer::ConfigureMediaType() {
    HRESULT hr;

    // Get native format info first
    IMFMediaType* pNativeType = nullptr;
    hr = m_sourceReader->GetNativeMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, &pNativeType);
    if (SUCCEEDED(hr)) {
        GUID subtype;
        if (SUCCEEDED(pNativeType->GetGUID(MF_MT_SUBTYPE, &subtype))) {
            m_nativeFormat = subtype;
            m_isNV12Native = (subtype == MFVideoFormat_NV12);
        }
        pNativeType->Release();
    }

    // Create desired output type (NV12)
    hr = MFCreateMediaType(&m_outputType);
    if (FAILED(hr)) return false;

    hr = m_outputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (FAILED(hr)) return false;

    // Request NV12 format - Media Foundation will convert if needed
    hr = m_outputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    if (FAILED(hr)) return false;

    // Set frame size
    hr = MFSetAttributeSize(m_outputType, MF_MT_FRAME_SIZE, m_requestedWidth, m_requestedHeight);
    if (FAILED(hr)) return false;

    // Set frame rate
    hr = MFSetAttributeRatio(m_outputType, MF_MT_FRAME_RATE, m_requestedFps, 1);
    if (FAILED(hr)) return false;

    // Set interlace mode
    hr = m_outputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (FAILED(hr)) return false;

    // Apply to source reader
    hr = m_sourceReader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, nullptr, m_outputType);
    if (FAILED(hr)) {
        std::cerr << "CameraCapturer: Failed to set media type (0x" << std::hex << hr << std::dec << ")\n";

        // Try without specifying size - use camera's default
        m_outputType->Release();
        hr = MFCreateMediaType(&m_outputType);
        if (FAILED(hr)) return false;

        hr = m_outputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        hr = m_outputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
        hr = m_sourceReader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, nullptr, m_outputType);

        if (FAILED(hr)) {
            std::cerr << "CameraCapturer: Failed to set any NV12 media type\n";
            return false;
        }
    }

    // Get the actual format that was set
    IMFMediaType* pActualType = nullptr;
    hr = m_sourceReader->GetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, &pActualType);
    if (SUCCEEDED(hr)) {
        UINT32 actualWidth = 0, actualHeight = 0;
        MFGetAttributeSize(pActualType, MF_MT_FRAME_SIZE, &actualWidth, &actualHeight);
        m_width = static_cast<int>(actualWidth);
        m_height = static_cast<int>(actualHeight);
        pActualType->Release();
    } else {
        // Fallback to requested
        m_width = m_requestedWidth;
        m_height = m_requestedHeight;
    }

    return true;
}

void CameraCapturer::Start(CameraFrameCallback callback) {
    if (m_running) return;

    m_callback = callback;
    m_running = true;

    QueryPerformanceCounter(&m_startTime);
    m_captureThread = std::thread(&CameraCapturer::CaptureLoop, this);
}

void CameraCapturer::Stop() {
    if (!m_running) return;

    m_running = false;

    if (m_captureThread.joinable()) {
        m_captureThread.join();
    }
}

void CameraCapturer::CaptureLoop() {
    HRESULT hr;
    uint64_t frameCount = 0;
    auto frameSize = CalculateNV12FrameSize(m_width, m_height);
    std::vector<uint8_t> nv12Buffer(frameSize);

    std::cerr << "CameraCapturer: Capture loop starting\n";

    while (m_running) {
        DWORD streamIndex, flags;
        LONGLONG timestamp;
        IMFSample* pSample = nullptr;

        hr = m_sourceReader->ReadSample(
            MF_SOURCE_READER_FIRST_VIDEO_STREAM,
            0,
            &streamIndex,
            &flags,
            &timestamp,
            &pSample
        );

        if (FAILED(hr)) {
            std::cerr << "CameraCapturer: ReadSample failed (0x" << std::hex << hr << std::dec << ")\n";
            Sleep(10);
            continue;
        }

        if (flags & MF_SOURCE_READERF_ENDOFSTREAM) {
            std::cerr << "CameraCapturer: End of stream\n";
            break;
        }

        if (flags & MF_SOURCE_READERF_STREAMTICK) {
            // Stream tick, skip
            if (pSample) pSample->Release();
            continue;
        }

        if (!pSample) {
            continue;
        }

        // Get sample buffer
        IMFMediaBuffer* pBuffer = nullptr;
        hr = pSample->ConvertToContiguousBuffer(&pBuffer);
        if (SUCCEEDED(hr)) {
            BYTE* pData = nullptr;
            DWORD dataLength = 0;

            hr = pBuffer->Lock(&pData, nullptr, &dataLength);
            if (SUCCEEDED(hr)) {
                // Calculate timestamp in milliseconds
                LARGE_INTEGER currentTime;
                QueryPerformanceCounter(&currentTime);
                uint64_t elapsedMs = static_cast<uint64_t>(
                    (currentTime.QuadPart - m_startTime.QuadPart) * 1000 / m_frequency.QuadPart
                );

                // Copy to output buffer (should already be NV12)
                size_t copySize = std::min(static_cast<size_t>(dataLength), frameSize);
                memcpy(nv12Buffer.data(), pData, copySize);

                // If data is smaller than expected, zero-fill the rest
                if (copySize < frameSize) {
                    memset(nv12Buffer.data() + copySize, 128, frameSize - copySize);  // 128 for UV planes
                }

                frameCount++;
                if (frameCount <= 5 || frameCount % 100 == 0) {
                    std::cerr << "CameraCapturer: Frame " << frameCount
                              << " (" << m_width << "x" << m_height << " NV12, " << dataLength << " bytes)\n";
                }

                // Call callback with NV12 data
                if (m_callback) {
                    m_callback(nv12Buffer.data(), frameSize, elapsedMs);
                }

                pBuffer->Unlock();
            }
            pBuffer->Release();
        }

        pSample->Release();
    }

    std::cerr << "CameraCapturer: Capture loop ended (" << frameCount << " frames)\n";
}

bool CameraCapturer::ConvertToNV12(IMFSample* sample, std::vector<uint8_t>& outNV12) {
    // This method is currently unused as we configure the source reader to output NV12 directly.
    // If needed in the future for cameras that don't support NV12, implement YUYV/RGB to NV12 conversion here.
    return false;
}

}  // namespace snacka
