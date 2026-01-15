#pragma once

#include <pulse/pulseaudio.h>
#include <functional>
#include <thread>
#include <atomic>
#include <vector>
#include <cstdint>
#include <string>
#include <mutex>

namespace snacka {

/// Callback for captured audio
/// @param data Pointer to PCM audio data (16-bit stereo interleaved)
/// @param sampleCount Number of stereo sample frames
/// @param timestamp Timestamp in milliseconds
using AudioCallback = std::function<void(const int16_t* data, size_t sampleCount, uint64_t timestamp)>;

/// PulseAudio capturer for system audio capture
/// Works on both PulseAudio and PipeWire (via PulseAudio compatibility)
class PulseAudioCapturer {
public:
    PulseAudioCapturer();
    ~PulseAudioCapturer();

    /// Initialize the audio capturer
    /// @return true if initialization succeeded
    bool Initialize();

    /// Start capturing audio
    /// @param callback Callback to receive captured audio
    void Start(AudioCallback callback);

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

private:
    // PulseAudio callbacks (static to work with C API)
    static void ContextStateCallback(pa_context* c, void* userdata);
    static void ServerInfoCallback(pa_context* c, const pa_server_info* info, void* userdata);
    static void SinkInfoCallback(pa_context* c, const pa_sink_info* info, int eol, void* userdata);
    static void StreamReadCallback(pa_stream* s, size_t length, void* userdata);
    static void StreamStateCallback(pa_stream* s, void* userdata);

    // Internal methods
    void MainLoop();
    void ProcessAudio(const void* data, size_t length);
    uint64_t GetTimestampMs() const;

    // PulseAudio objects
    pa_threaded_mainloop* m_mainloop = nullptr;
    pa_context* m_context = nullptr;
    pa_stream* m_stream = nullptr;

    // Monitor source name (e.g., "alsa_output.pci-0000_00_1f.3.analog-stereo.monitor")
    std::string m_monitorSource;

    // Thread control
    std::atomic<bool> m_running{false};
    std::atomic<bool> m_contextReady{false};
    std::atomic<bool> m_streamReady{false};

    // Callback
    AudioCallback m_callback;
    std::mutex m_callbackMutex;

    // Resampling buffer (if source sample rate differs from 48kHz)
    std::vector<int16_t> m_resampleBuffer;
    uint32_t m_sourceSampleRate = 48000;
};

}  // namespace snacka
