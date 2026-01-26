#include "MicrophoneCapturer.h"
#include <iostream>
#include <cmath>
#include <algorithm>
#include <functiondiscoverykeys_devpkey.h>

#pragma comment(lib, "ole32.lib")

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

MicrophoneCapturer::MicrophoneCapturer() {
    QueryPerformanceFrequency(&m_frequency);
}

MicrophoneCapturer::~MicrophoneCapturer() {
    Stop();
    if (m_waveFormat) {
        CoTaskMemFree(m_waveFormat);
    }
}

std::vector<MicrophoneInfo> MicrophoneCapturer::EnumerateMicrophones() {
    std::vector<MicrophoneInfo> microphones;

    // Create device enumerator
    ComPtr<IMMDeviceEnumerator> deviceEnumerator;
    HRESULT hr = CoCreateInstance(
        __uuidof(MMDeviceEnumerator),
        nullptr,
        CLSCTX_ALL,
        __uuidof(IMMDeviceEnumerator),
        reinterpret_cast<void**>(deviceEnumerator.GetAddressOf())
    );
    if (FAILED(hr)) {
        std::cerr << "MicrophoneCapturer: Failed to create device enumerator\n";
        return microphones;
    }

    // Enumerate capture devices (microphones)
    ComPtr<IMMDeviceCollection> deviceCollection;
    hr = deviceEnumerator->EnumAudioEndpoints(
        eCapture,  // Capture devices (microphones)
        DEVICE_STATE_ACTIVE,
        &deviceCollection
    );
    if (FAILED(hr)) {
        std::cerr << "MicrophoneCapturer: Failed to enumerate capture devices\n";
        return microphones;
    }

    UINT count = 0;
    deviceCollection->GetCount(&count);

    for (UINT i = 0; i < count; i++) {
        ComPtr<IMMDevice> device;
        hr = deviceCollection->Item(i, &device);
        if (FAILED(hr)) continue;

        MicrophoneInfo info;
        info.index = static_cast<int>(i);

        // Get device ID
        LPWSTR deviceId = nullptr;
        hr = device->GetId(&deviceId);
        if (SUCCEEDED(hr) && deviceId) {
            info.id = WideToUtf8(deviceId);
            CoTaskMemFree(deviceId);
        } else {
            info.id = std::to_string(i);
        }

        // Get friendly name
        ComPtr<IPropertyStore> props;
        hr = device->OpenPropertyStore(STGM_READ, &props);
        if (SUCCEEDED(hr)) {
            PROPVARIANT varName;
            PropVariantInit(&varName);
            hr = props->GetValue(PKEY_Device_FriendlyName, &varName);
            if (SUCCEEDED(hr) && varName.vt == VT_LPWSTR) {
                info.name = WideToUtf8(varName.pwszVal);
            }
            PropVariantClear(&varName);
        }

        if (info.name.empty()) {
            info.name = "Microphone " + std::to_string(i + 1);
        }

        microphones.push_back(info);
    }

    return microphones;
}

ComPtr<IMMDevice> MicrophoneCapturer::FindMicrophone(const std::string& idOrIndex) {
    ComPtr<IMMDeviceEnumerator> deviceEnumerator;
    HRESULT hr = CoCreateInstance(
        __uuidof(MMDeviceEnumerator),
        nullptr,
        CLSCTX_ALL,
        __uuidof(IMMDeviceEnumerator),
        reinterpret_cast<void**>(deviceEnumerator.GetAddressOf())
    );
    if (FAILED(hr)) {
        return nullptr;
    }

    // If empty, return default device
    if (idOrIndex.empty()) {
        ComPtr<IMMDevice> device;
        hr = deviceEnumerator->GetDefaultAudioEndpoint(eCapture, eConsole, &device);
        if (SUCCEEDED(hr)) {
            return device;
        }
        return nullptr;
    }

    // Try to find by device ID first
    {
        ComPtr<IMMDevice> device;
        std::wstring wideId = Utf8ToWide(idOrIndex);
        hr = deviceEnumerator->GetDevice(wideId.c_str(), &device);
        if (SUCCEEDED(hr)) {
            return device;
        }
    }

    // Try to find by index
    try {
        int index = std::stoi(idOrIndex);
        auto microphones = EnumerateMicrophones();
        if (index >= 0 && index < static_cast<int>(microphones.size())) {
            ComPtr<IMMDevice> device;
            std::wstring wideId = Utf8ToWide(microphones[index].id);
            hr = deviceEnumerator->GetDevice(wideId.c_str(), &device);
            if (SUCCEEDED(hr)) {
                return device;
            }
        }
    } catch (...) {
        // Not a valid index
    }

    return nullptr;
}

bool MicrophoneCapturer::Initialize(const std::string& deviceIdOrIndex) {
    HRESULT hr;

    // Create device enumerator
    hr = CoCreateInstance(
        __uuidof(MMDeviceEnumerator),
        nullptr,
        CLSCTX_ALL,
        __uuidof(IMMDeviceEnumerator),
        reinterpret_cast<void**>(m_deviceEnumerator.GetAddressOf())
    );
    if (FAILED(hr)) {
        std::cerr << "MicrophoneCapturer: Failed to create device enumerator\n";
        return false;
    }

    // Find microphone device
    m_device = FindMicrophone(deviceIdOrIndex);
    if (!m_device) {
        // Fall back to default capture device
        hr = m_deviceEnumerator->GetDefaultAudioEndpoint(
            eCapture,  // Capture device (microphone)
            eConsole,
            &m_device
        );
        if (FAILED(hr)) {
            std::cerr << "MicrophoneCapturer: Failed to get default microphone endpoint\n";
            return false;
        }
    }

    // Get device name for logging
    ComPtr<IPropertyStore> props;
    m_device->OpenPropertyStore(STGM_READ, &props);
    if (props) {
        PROPVARIANT varName;
        PropVariantInit(&varName);
        props->GetValue(PKEY_Device_FriendlyName, &varName);
        if (varName.vt == VT_LPWSTR) {
            std::wcerr << L"MicrophoneCapturer: Using microphone: " << varName.pwszVal << L"\n";
        }
        PropVariantClear(&varName);
    }

    // Create audio client
    hr = m_device->Activate(
        __uuidof(IAudioClient),
        CLSCTX_ALL,
        nullptr,
        reinterpret_cast<void**>(m_audioClient.GetAddressOf())
    );
    if (FAILED(hr)) {
        std::cerr << "MicrophoneCapturer: Failed to activate audio client\n";
        return false;
    }

    // Get the mix format (native format of the microphone)
    hr = m_audioClient->GetMixFormat(&m_waveFormat);
    if (FAILED(hr)) {
        std::cerr << "MicrophoneCapturer: Failed to get mix format\n";
        return false;
    }

    // Parse format info
    m_channels = m_waveFormat->nChannels;
    m_sampleRate = m_waveFormat->nSamplesPerSec;
    m_bitsPerSample = m_waveFormat->wBitsPerSample;

    // Check for float format
    if (m_waveFormat->wFormatTag == WAVE_FORMAT_IEEE_FLOAT) {
        m_isFloat = true;
    } else if (m_waveFormat->wFormatTag == WAVE_FORMAT_EXTENSIBLE) {
        auto* extFormat = reinterpret_cast<WAVEFORMATEXTENSIBLE*>(m_waveFormat);
        m_isFloat = (extFormat->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
    }

    std::cerr << "MicrophoneCapturer: Audio format: "
              << m_sampleRate << "Hz, "
              << m_bitsPerSample << "-bit, "
              << m_channels << "ch, "
              << (m_isFloat ? "float" : "int") << "\n";

    // Initialize audio client in standard capture mode (NOT loopback)
    // Use 20ms buffer
    REFERENCE_TIME bufferDuration = 200000;  // 20ms in 100ns units

    hr = m_audioClient->Initialize(
        AUDCLNT_SHAREMODE_SHARED,
        0,  // No special flags - regular capture mode (not loopback)
        bufferDuration,
        0,
        m_waveFormat,
        nullptr
    );
    if (FAILED(hr)) {
        std::cerr << "MicrophoneCapturer: Failed to initialize audio client: 0x"
                  << std::hex << hr << std::dec << "\n";
        return false;
    }

    // Get capture client
    hr = m_audioClient->GetService(
        __uuidof(IAudioCaptureClient),
        reinterpret_cast<void**>(m_captureClient.GetAddressOf())
    );
    if (FAILED(hr)) {
        std::cerr << "MicrophoneCapturer: Failed to get capture client\n";
        return false;
    }

    std::cerr << "MicrophoneCapturer: Microphone capture initialized (WASAPI)\n";
    return true;
}

void MicrophoneCapturer::Start(MicrophoneCallback callback) {
    if (m_running) return;

    m_callback = callback;
    m_running = true;

    // Start the audio stream
    HRESULT hr = m_audioClient->Start();
    if (FAILED(hr)) {
        std::cerr << "MicrophoneCapturer: Failed to start audio capture\n";
        m_running = false;
        return;
    }

    QueryPerformanceCounter(&m_startTime);

    m_captureThread = std::thread([this]() { CaptureLoop(); });
}

void MicrophoneCapturer::Stop() {
    if (!m_running) return;

    m_running = false;

    if (m_audioClient) {
        m_audioClient->Stop();
    }

    if (m_captureThread.joinable()) {
        m_captureThread.join();
    }
}

void MicrophoneCapturer::CaptureLoop() {
    while (m_running) {
        UINT32 packetLength = 0;
        HRESULT hr = m_captureClient->GetNextPacketSize(&packetLength);

        if (FAILED(hr)) {
            std::cerr << "MicrophoneCapturer: GetNextPacketSize failed\n";
            break;
        }

        while (packetLength > 0 && m_running) {
            BYTE* data;
            UINT32 numFrames;
            DWORD flags;
            UINT64 devicePosition;
            UINT64 qpcPosition;

            hr = m_captureClient->GetBuffer(
                &data,
                &numFrames,
                &flags,
                &devicePosition,
                &qpcPosition
            );

            if (FAILED(hr)) {
                std::cerr << "MicrophoneCapturer: GetBuffer failed\n";
                break;
            }

            // Calculate timestamp in milliseconds
            LARGE_INTEGER now;
            QueryPerformanceCounter(&now);
            uint64_t timestamp = static_cast<uint64_t>(
                (now.QuadPart - m_startTime.QuadPart) * 1000 / m_frequency.QuadPart);

            if (flags & AUDCLNT_BUFFERFLAGS_SILENT) {
                // Silent buffer - output silence
                size_t outputFrames = numFrames;
                if (m_sampleRate != 48000) {
                    outputFrames = static_cast<size_t>(numFrames * 48000.0 / m_sampleRate);
                }
                m_outputBuffer.resize(outputFrames * 2);  // Stereo
                std::fill(m_outputBuffer.begin(), m_outputBuffer.end(), 0);
            } else {
                // Normalize audio to 48kHz 16-bit stereo
                NormalizeAudio(data, numFrames, m_outputBuffer);
            }

            m_captureClient->ReleaseBuffer(numFrames);

            // Send to callback with MCAP header
            if (m_callback && !m_outputBuffer.empty()) {
                // Create header
                AudioPacketHeader header(
                    static_cast<uint32_t>(m_outputBuffer.size() / 2),  // Stereo frames
                    timestamp
                );

                // Combine header and audio data
                size_t totalSize = sizeof(header) + m_outputBuffer.size() * sizeof(int16_t);
                std::vector<uint8_t> packet(totalSize);
                memcpy(packet.data(), &header, sizeof(header));
                memcpy(packet.data() + sizeof(header), m_outputBuffer.data(),
                       m_outputBuffer.size() * sizeof(int16_t));

                m_callback(packet.data(), packet.size(), timestamp);
            }

            hr = m_captureClient->GetNextPacketSize(&packetLength);
            if (FAILED(hr)) break;
        }

        // Sleep a bit to avoid busy waiting
        Sleep(5);
    }
}

void MicrophoneCapturer::NormalizeAudio(const BYTE* inputData, UINT32 numFrames,
                                         std::vector<int16_t>& outputBuffer) {
    // First convert to float for processing
    m_resampleBuffer.resize(numFrames * 2);  // Always convert to stereo float first

    const int bytesPerSample = m_bitsPerSample / 8;
    const int bytesPerFrame = bytesPerSample * m_channels;

    for (UINT32 i = 0; i < numFrames; i++) {
        const BYTE* frame = inputData + i * bytesPerFrame;

        // Read left channel (or mono)
        float left = 0.0f;
        float right = 0.0f;

        if (m_isFloat && m_bitsPerSample == 32) {
            left = *reinterpret_cast<const float*>(frame);
            if (m_channels >= 2) {
                right = *reinterpret_cast<const float*>(frame + 4);
            } else {
                right = left;
            }
        } else if (m_bitsPerSample == 16) {
            left = *reinterpret_cast<const int16_t*>(frame) / 32768.0f;
            if (m_channels >= 2) {
                right = *reinterpret_cast<const int16_t*>(frame + 2) / 32768.0f;
            } else {
                right = left;
            }
        } else if (m_bitsPerSample == 32 && !m_isFloat) {
            left = *reinterpret_cast<const int32_t*>(frame) / 2147483648.0f;
            if (m_channels >= 2) {
                right = *reinterpret_cast<const int32_t*>(frame + 4) / 2147483648.0f;
            } else {
                right = left;
            }
        } else if (m_bitsPerSample == 24) {
            // 24-bit packed
            int32_t sample = (frame[2] << 24) | (frame[1] << 16) | (frame[0] << 8);
            left = sample / 2147483648.0f;
            if (m_channels >= 2) {
                sample = (frame[5] << 24) | (frame[4] << 16) | (frame[3] << 8);
                right = sample / 2147483648.0f;
            } else {
                right = left;
            }
        }

        m_resampleBuffer[i * 2] = left;
        m_resampleBuffer[i * 2 + 1] = right;
    }

    // Resample to 48kHz if needed
    size_t outputFrames;
    if (m_sampleRate == 48000) {
        outputFrames = numFrames;
    } else {
        // Simple linear interpolation resampling
        double ratio = 48000.0 / m_sampleRate;
        outputFrames = static_cast<size_t>(numFrames * ratio);
    }

    outputBuffer.resize(outputFrames * 2);

    if (m_sampleRate == 48000) {
        // Direct conversion to int16
        for (size_t i = 0; i < outputFrames * 2; i++) {
            float sample = m_resampleBuffer[i];
            sample = std::clamp(sample, -1.0f, 1.0f);
            outputBuffer[i] = static_cast<int16_t>(sample * 32767.0f);
        }
    } else {
        // Resample with linear interpolation
        double ratio = static_cast<double>(numFrames - 1) / (outputFrames - 1);
        for (size_t i = 0; i < outputFrames; i++) {
            double srcPos = i * ratio;
            size_t srcIndex = static_cast<size_t>(srcPos);
            double frac = srcPos - srcIndex;

            // Clamp to valid range
            if (srcIndex >= numFrames - 1) {
                srcIndex = numFrames - 2;
                frac = 1.0;
            }

            // Interpolate left
            float left = static_cast<float>(
                m_resampleBuffer[srcIndex * 2] * (1.0 - frac) +
                m_resampleBuffer[(srcIndex + 1) * 2] * frac);

            // Interpolate right
            float right = static_cast<float>(
                m_resampleBuffer[srcIndex * 2 + 1] * (1.0 - frac) +
                m_resampleBuffer[(srcIndex + 1) * 2 + 1] * frac);

            left = std::clamp(left, -1.0f, 1.0f);
            right = std::clamp(right, -1.0f, 1.0f);

            outputBuffer[i * 2] = static_cast<int16_t>(left * 32767.0f);
            outputBuffer[i * 2 + 1] = static_cast<int16_t>(right * 32767.0f);
        }
    }
}

}  // namespace snacka
