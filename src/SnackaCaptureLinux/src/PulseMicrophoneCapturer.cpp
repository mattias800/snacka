#include "PulseMicrophoneCapturer.h"
#include <iostream>
#include <cstring>
#include <ctime>
#include <algorithm>

namespace snacka {

// Static members for enumeration
std::vector<MicrophoneInfo>* PulseMicrophoneCapturer::s_enumeratedMicrophones = nullptr;
std::mutex PulseMicrophoneCapturer::s_enumerationMutex;

PulseMicrophoneCapturer::PulseMicrophoneCapturer() = default;

PulseMicrophoneCapturer::~PulseMicrophoneCapturer() {
    Stop();
}

std::vector<MicrophoneInfo> PulseMicrophoneCapturer::EnumerateMicrophones() {
    std::vector<MicrophoneInfo> microphones;

    // Create a temporary mainloop and context for enumeration
    pa_mainloop* mainloop = pa_mainloop_new();
    if (!mainloop) {
        return microphones;
    }

    pa_mainloop_api* api = pa_mainloop_get_api(mainloop);
    pa_context* context = pa_context_new(api, "SnackaCaptureLinux-Enum");
    if (!context) {
        pa_mainloop_free(mainloop);
        return microphones;
    }

    // Connect to PulseAudio
    if (pa_context_connect(context, nullptr, PA_CONTEXT_NOFLAGS, nullptr) < 0) {
        pa_context_unref(context);
        pa_mainloop_free(mainloop);
        return microphones;
    }

    // Wait for connection
    pa_context_state_t state;
    while ((state = pa_context_get_state(context)) != PA_CONTEXT_READY) {
        if (state == PA_CONTEXT_FAILED || state == PA_CONTEXT_TERMINATED) {
            pa_context_unref(context);
            pa_mainloop_free(mainloop);
            return microphones;
        }
        pa_mainloop_iterate(mainloop, 1, nullptr);
    }

    // Set up static pointer for callback
    {
        std::lock_guard<std::mutex> lock(s_enumerationMutex);
        s_enumeratedMicrophones = &microphones;
    }

    // Enumerate sources
    pa_operation* op = pa_context_get_source_info_list(context,
        [](pa_context*, const pa_source_info* info, int eol, void*) {
            if (eol > 0 || !info) {
                return;
            }

            // Skip monitor sources (they end with .monitor)
            std::string name = info->name ? info->name : "";
            if (name.find(".monitor") != std::string::npos) {
                return;
            }

            std::lock_guard<std::mutex> lock(s_enumerationMutex);
            if (s_enumeratedMicrophones) {
                MicrophoneInfo mic;
                mic.id = name;
                mic.name = info->description ? info->description : name;
                mic.index = static_cast<int>(s_enumeratedMicrophones->size());
                s_enumeratedMicrophones->push_back(mic);
            }
        },
        nullptr
    );

    // Wait for enumeration to complete
    while (op && pa_operation_get_state(op) == PA_OPERATION_RUNNING) {
        pa_mainloop_iterate(mainloop, 1, nullptr);
    }
    if (op) {
        pa_operation_unref(op);
    }

    // Clean up static pointer
    {
        std::lock_guard<std::mutex> lock(s_enumerationMutex);
        s_enumeratedMicrophones = nullptr;
    }

    // Disconnect and clean up
    pa_context_disconnect(context);
    pa_context_unref(context);
    pa_mainloop_free(mainloop);

    return microphones;
}

bool PulseMicrophoneCapturer::Initialize(const std::string& sourceIdOrIndex) {
    std::cerr << "PulseMicrophoneCapturer: Initializing...\n";

    m_requestedSource = sourceIdOrIndex;

    // Create threaded mainloop
    m_mainloop = pa_threaded_mainloop_new();
    if (!m_mainloop) {
        std::cerr << "PulseMicrophoneCapturer: Failed to create mainloop\n";
        return false;
    }

    // Create context
    pa_mainloop_api* api = pa_threaded_mainloop_get_api(m_mainloop);
    m_context = pa_context_new(api, "SnackaCaptureLinux-Mic");
    if (!m_context) {
        std::cerr << "PulseMicrophoneCapturer: Failed to create context\n";
        pa_threaded_mainloop_free(m_mainloop);
        m_mainloop = nullptr;
        return false;
    }

    // Set context state callback
    pa_context_set_state_callback(m_context, ContextStateCallback, this);

    // Connect to PulseAudio server
    if (pa_context_connect(m_context, nullptr, PA_CONTEXT_NOFLAGS, nullptr) < 0) {
        std::cerr << "PulseMicrophoneCapturer: Failed to connect to PulseAudio server\n";
        pa_context_unref(m_context);
        m_context = nullptr;
        pa_threaded_mainloop_free(m_mainloop);
        m_mainloop = nullptr;
        return false;
    }

    // Start the mainloop
    if (pa_threaded_mainloop_start(m_mainloop) < 0) {
        std::cerr << "PulseMicrophoneCapturer: Failed to start mainloop\n";
        pa_context_disconnect(m_context);
        pa_context_unref(m_context);
        m_context = nullptr;
        pa_threaded_mainloop_free(m_mainloop);
        m_mainloop = nullptr;
        return false;
    }

    // Wait for context to be ready
    pa_threaded_mainloop_lock(m_mainloop);
    while (!m_contextReady) {
        pa_context_state_t state = pa_context_get_state(m_context);
        if (state == PA_CONTEXT_READY) {
            m_contextReady = true;
            break;
        } else if (state == PA_CONTEXT_FAILED || state == PA_CONTEXT_TERMINATED) {
            std::cerr << "PulseMicrophoneCapturer: Context connection failed\n";
            pa_threaded_mainloop_unlock(m_mainloop);
            Stop();
            return false;
        }
        pa_threaded_mainloop_wait(m_mainloop);
    }

    // Find the source to use
    m_sourceFound = false;
    pa_operation* op = pa_context_get_source_info_list(m_context, SourceInfoCallback, this);
    while (pa_operation_get_state(op) == PA_OPERATION_RUNNING) {
        pa_threaded_mainloop_wait(m_mainloop);
    }
    pa_operation_unref(op);

    pa_threaded_mainloop_unlock(m_mainloop);

    if (m_sourceName.empty()) {
        std::cerr << "PulseMicrophoneCapturer: Failed to find microphone source\n";
        Stop();
        return false;
    }

    std::cerr << "PulseMicrophoneCapturer: Using microphone source: " << m_sourceName << "\n";
    return true;
}

void PulseMicrophoneCapturer::Start(MicrophoneCallback callback) {
    if (m_running || !m_context) {
        return;
    }

    {
        std::lock_guard<std::mutex> lock(m_callbackMutex);
        m_callback = callback;
    }

    pa_threaded_mainloop_lock(m_mainloop);

    // Create sample spec for 48kHz stereo 16-bit
    pa_sample_spec sampleSpec;
    sampleSpec.format = PA_SAMPLE_S16LE;
    sampleSpec.rate = 48000;
    sampleSpec.channels = 2;

    // Create stream
    m_stream = pa_stream_new(m_context, "SnackaCaptureLinux Microphone", &sampleSpec, nullptr);
    if (!m_stream) {
        std::cerr << "PulseMicrophoneCapturer: Failed to create stream\n";
        pa_threaded_mainloop_unlock(m_mainloop);
        return;
    }

    // Set stream callbacks
    pa_stream_set_state_callback(m_stream, StreamStateCallback, this);
    pa_stream_set_read_callback(m_stream, StreamReadCallback, this);

    // Buffer attributes for low latency
    pa_buffer_attr bufferAttr;
    bufferAttr.maxlength = (uint32_t)-1;
    bufferAttr.tlength = (uint32_t)-1;
    bufferAttr.prebuf = (uint32_t)-1;
    bufferAttr.minreq = (uint32_t)-1;
    bufferAttr.fragsize = pa_usec_to_bytes(20000, &sampleSpec);  // 20ms fragments

    // Connect stream to microphone source
    int flags = PA_STREAM_ADJUST_LATENCY | PA_STREAM_AUTO_TIMING_UPDATE;
    if (pa_stream_connect_record(m_stream, m_sourceName.c_str(), &bufferAttr,
                                  static_cast<pa_stream_flags_t>(flags)) < 0) {
        std::cerr << "PulseMicrophoneCapturer: Failed to connect stream: "
                  << pa_strerror(pa_context_errno(m_context)) << "\n";
        pa_stream_unref(m_stream);
        m_stream = nullptr;
        pa_threaded_mainloop_unlock(m_mainloop);
        return;
    }

    // Wait for stream to be ready
    while (!m_streamReady) {
        pa_stream_state_t state = pa_stream_get_state(m_stream);
        if (state == PA_STREAM_READY) {
            m_streamReady = true;
            break;
        } else if (state == PA_STREAM_FAILED || state == PA_STREAM_TERMINATED) {
            std::cerr << "PulseMicrophoneCapturer: Stream connection failed\n";
            pa_stream_unref(m_stream);
            m_stream = nullptr;
            pa_threaded_mainloop_unlock(m_mainloop);
            return;
        }
        pa_threaded_mainloop_wait(m_mainloop);
    }

    pa_threaded_mainloop_unlock(m_mainloop);

    m_running = true;
    std::cerr << "PulseMicrophoneCapturer: Microphone capture started (48kHz stereo 16-bit)\n";
}

void PulseMicrophoneCapturer::Stop() {
    m_running = false;

    if (m_mainloop) {
        pa_threaded_mainloop_lock(m_mainloop);

        if (m_stream) {
            pa_stream_disconnect(m_stream);
            pa_stream_unref(m_stream);
            m_stream = nullptr;
        }

        if (m_context) {
            pa_context_disconnect(m_context);
            pa_context_unref(m_context);
            m_context = nullptr;
        }

        pa_threaded_mainloop_unlock(m_mainloop);
        pa_threaded_mainloop_stop(m_mainloop);
        pa_threaded_mainloop_free(m_mainloop);
        m_mainloop = nullptr;
    }

    m_contextReady = false;
    m_streamReady = false;
    m_sourceFound = false;
    m_sourceName.clear();

    std::cerr << "PulseMicrophoneCapturer: Stopped\n";
}

void PulseMicrophoneCapturer::ContextStateCallback(pa_context* c, void* userdata) {
    auto* self = static_cast<PulseMicrophoneCapturer*>(userdata);
    pa_context_state_t state = pa_context_get_state(c);

    switch (state) {
        case PA_CONTEXT_READY:
            self->m_contextReady = true;
            pa_threaded_mainloop_signal(self->m_mainloop, 0);
            break;
        case PA_CONTEXT_FAILED:
        case PA_CONTEXT_TERMINATED:
            pa_threaded_mainloop_signal(self->m_mainloop, 0);
            break;
        default:
            break;
    }
}

void PulseMicrophoneCapturer::SourceInfoCallback(pa_context* c, const pa_source_info* info, int eol, void* userdata) {
    auto* self = static_cast<PulseMicrophoneCapturer*>(userdata);

    if (eol > 0) {
        // End of list - signal completion
        pa_threaded_mainloop_signal(self->m_mainloop, 0);
        return;
    }

    if (!info || !info->name) {
        return;
    }

    std::string name = info->name;
    std::string description = info->description ? info->description : name;

    // Skip monitor sources
    if (name.find(".monitor") != std::string::npos) {
        return;
    }

    // Check if this is the requested source
    bool matches = false;
    if (self->m_requestedSource.empty()) {
        // No specific request - use the first non-monitor source as default
        if (self->m_sourceName.empty()) {
            matches = true;
        }
    } else {
        // Check if it matches by name
        if (name == self->m_requestedSource) {
            matches = true;
        }
        // Check if it matches by index
        else {
            try {
                int requestedIndex = std::stoi(self->m_requestedSource);
                // Count non-monitor sources to find index
                static int currentIndex = 0;
                if (!self->m_sourceFound && currentIndex == requestedIndex) {
                    matches = true;
                }
                currentIndex++;
            } catch (...) {
                // Not a valid index
            }
        }
    }

    if (matches && self->m_sourceName.empty()) {
        self->m_sourceName = name;
        self->m_sourceFound = true;
        std::cerr << "PulseMicrophoneCapturer: Found microphone: " << description
                  << " (" << name << ")\n";
    }
}

void PulseMicrophoneCapturer::StreamStateCallback(pa_stream* s, void* userdata) {
    auto* self = static_cast<PulseMicrophoneCapturer*>(userdata);
    pa_stream_state_t state = pa_stream_get_state(s);

    switch (state) {
        case PA_STREAM_READY:
            self->m_streamReady = true;
            pa_threaded_mainloop_signal(self->m_mainloop, 0);
            break;
        case PA_STREAM_FAILED:
        case PA_STREAM_TERMINATED:
            pa_threaded_mainloop_signal(self->m_mainloop, 0);
            break;
        default:
            break;
    }
}

void PulseMicrophoneCapturer::StreamReadCallback(pa_stream* s, size_t length, void* userdata) {
    auto* self = static_cast<PulseMicrophoneCapturer*>(userdata);

    if (!self->m_running) {
        return;
    }

    const void* data;
    size_t nbytes;

    // Peek at the data
    if (pa_stream_peek(s, &data, &nbytes) < 0) {
        std::cerr << "PulseMicrophoneCapturer: Failed to peek stream data\n";
        return;
    }

    if (data && nbytes > 0) {
        self->ProcessAudio(data, nbytes);
    }

    // Drop the data
    pa_stream_drop(s);
}

void PulseMicrophoneCapturer::ProcessAudio(const void* data, size_t length) {
    if (!data || length == 0) {
        return;
    }

    // Data is already 16-bit stereo (4 bytes per frame)
    const int16_t* samples = static_cast<const int16_t*>(data);
    size_t sampleCount = length / 4;  // 2 channels * 2 bytes per sample

    uint64_t timestamp = GetTimestampMs();

    std::lock_guard<std::mutex> lock(m_callbackMutex);
    if (m_callback) {
        m_callback(samples, sampleCount, timestamp);
    }
}

uint64_t PulseMicrophoneCapturer::GetTimestampMs() const {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return static_cast<uint64_t>(ts.tv_sec) * 1000 + ts.tv_nsec / 1000000;
}

}  // namespace snacka
