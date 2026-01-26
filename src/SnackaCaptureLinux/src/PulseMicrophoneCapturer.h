#pragma once

#include "Protocol.h"
#include <pulse/pulseaudio.h>
#include <functional>
#include <thread>
#include <atomic>
#include <vector>
#include <cstdint>
#include <string>
#include <mutex>

namespace snacka {

/// Callback for captured microphone audio
/// @param data Pointer to PCM audio data (16-bit stereo interleaved)
/// @param sampleCount Number of stereo sample frames
/// @param timestamp Timestamp in milliseconds
using MicrophoneCallback = std::function<void(const int16_t* data, size_t sampleCount, uint64_t timestamp)>;

/// PulseAudio capturer for microphone input
/// Captures from microphone sources (not monitor sources)
class PulseMicrophoneCapturer {
public:
    PulseMicrophoneCapturer();
    ~PulseMicrophoneCapturer();

    /// Initialize the microphone capturer
    /// @param sourceIdOrIndex Source name, or index as string, or empty for default
    /// @return true if initialization succeeded
    bool Initialize(const std::string& sourceIdOrIndex = "");

    /// Start capturing audio
    /// @param callback Callback to receive captured audio
    void Start(MicrophoneCallback callback);

    /// Stop capturing
    void Stop();

    /// Check if capturing is running
    bool IsRunning() const { return m_running; }

    /// Get the sample rate (always 48000)
    static constexpr uint32_t GetSampleRate() { return 48000; }

    /// Get the number of channels (always 2)
    static constexpr uint8_t GetChannels() { return 2; }

    /// Get bits per sample (always 16)
    static constexpr uint8_t GetBitsPerSample() { return 16; }

    /// Enumerate available microphone sources (non-monitor sources)
    static std::vector<MicrophoneInfo> EnumerateMicrophones();

private:
    // PulseAudio callbacks (static to work with C API)
    static void ContextStateCallback(pa_context* c, void* userdata);
    static void SourceInfoCallback(pa_context* c, const pa_source_info* info, int eol, void* userdata);
    static void StreamReadCallback(pa_stream* s, size_t length, void* userdata);
    static void StreamStateCallback(pa_stream* s, void* userdata);

    // Internal methods
    void ProcessAudio(const void* data, size_t length);
    uint64_t GetTimestampMs() const;

    // PulseAudio objects
    pa_threaded_mainloop* m_mainloop = nullptr;
    pa_context* m_context = nullptr;
    pa_stream* m_stream = nullptr;

    // Source name to capture from
    std::string m_sourceName;
    std::string m_requestedSource;

    // Thread control
    std::atomic<bool> m_running{false};
    std::atomic<bool> m_contextReady{false};
    std::atomic<bool> m_streamReady{false};
    std::atomic<bool> m_sourceFound{false};

    // Callback
    MicrophoneCallback m_callback;
    std::mutex m_callbackMutex;

    // Static data for enumeration callback
    static std::vector<MicrophoneInfo>* s_enumeratedMicrophones;
    static std::mutex s_enumerationMutex;
};

}  // namespace snacka
