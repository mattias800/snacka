#include "PulseAudioCapturer.h"
#include <iostream>
#include <cstring>
#include <ctime>

namespace snacka {

PulseAudioCapturer::PulseAudioCapturer() = default;

PulseAudioCapturer::~PulseAudioCapturer() {
    Stop();
}

bool PulseAudioCapturer::Initialize() {
    std::cerr << "PulseAudioCapturer: Initializing...\n";

    // Create threaded mainloop
    m_mainloop = pa_threaded_mainloop_new();
    if (!m_mainloop) {
        std::cerr << "PulseAudioCapturer: Failed to create mainloop\n";
        return false;
    }

    // Create context
    pa_mainloop_api* api = pa_threaded_mainloop_get_api(m_mainloop);
    m_context = pa_context_new(api, "SnackaCaptureLinux");
    if (!m_context) {
        std::cerr << "PulseAudioCapturer: Failed to create context\n";
        pa_threaded_mainloop_free(m_mainloop);
        m_mainloop = nullptr;
        return false;
    }

    // Set context state callback
    pa_context_set_state_callback(m_context, ContextStateCallback, this);

    // Connect to PulseAudio server
    if (pa_context_connect(m_context, nullptr, PA_CONTEXT_NOFLAGS, nullptr) < 0) {
        std::cerr << "PulseAudioCapturer: Failed to connect to PulseAudio server\n";
        pa_context_unref(m_context);
        m_context = nullptr;
        pa_threaded_mainloop_free(m_mainloop);
        m_mainloop = nullptr;
        return false;
    }

    // Start the mainloop
    if (pa_threaded_mainloop_start(m_mainloop) < 0) {
        std::cerr << "PulseAudioCapturer: Failed to start mainloop\n";
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
            std::cerr << "PulseAudioCapturer: Context connection failed\n";
            pa_threaded_mainloop_unlock(m_mainloop);
            Stop();
            return false;
        }
        pa_threaded_mainloop_wait(m_mainloop);
    }

    // Get server info to find default sink
    pa_operation* op = pa_context_get_server_info(m_context, ServerInfoCallback, this);
    while (pa_operation_get_state(op) == PA_OPERATION_RUNNING) {
        pa_threaded_mainloop_wait(m_mainloop);
    }
    pa_operation_unref(op);

    pa_threaded_mainloop_unlock(m_mainloop);

    if (m_monitorSource.empty()) {
        std::cerr << "PulseAudioCapturer: Failed to find monitor source\n";
        Stop();
        return false;
    }

    std::cerr << "PulseAudioCapturer: Using monitor source: " << m_monitorSource << "\n";
    return true;
}

void PulseAudioCapturer::Start(AudioCallback callback) {
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
    m_stream = pa_stream_new(m_context, "SnackaCaptureLinux Audio", &sampleSpec, nullptr);
    if (!m_stream) {
        std::cerr << "PulseAudioCapturer: Failed to create stream\n";
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

    // Connect stream to monitor source
    int flags = PA_STREAM_ADJUST_LATENCY | PA_STREAM_AUTO_TIMING_UPDATE;
    if (pa_stream_connect_record(m_stream, m_monitorSource.c_str(), &bufferAttr,
                                  static_cast<pa_stream_flags_t>(flags)) < 0) {
        std::cerr << "PulseAudioCapturer: Failed to connect stream: "
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
            std::cerr << "PulseAudioCapturer: Stream connection failed\n";
            pa_stream_unref(m_stream);
            m_stream = nullptr;
            pa_threaded_mainloop_unlock(m_mainloop);
            return;
        }
        pa_threaded_mainloop_wait(m_mainloop);
    }

    pa_threaded_mainloop_unlock(m_mainloop);

    m_running = true;
    std::cerr << "PulseAudioCapturer: Audio capture started (48kHz stereo 16-bit)\n";
}

void PulseAudioCapturer::Stop() {
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
    m_monitorSource.clear();

    std::cerr << "PulseAudioCapturer: Stopped\n";
}

void PulseAudioCapturer::ContextStateCallback(pa_context* c, void* userdata) {
    auto* self = static_cast<PulseAudioCapturer*>(userdata);
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

void PulseAudioCapturer::ServerInfoCallback(pa_context* c, const pa_server_info* info, void* userdata) {
    auto* self = static_cast<PulseAudioCapturer*>(userdata);

    if (info && info->default_sink_name) {
        std::cerr << "PulseAudioCapturer: Default sink: " << info->default_sink_name << "\n";

        // Get sink info to find its monitor source
        pa_operation* op = pa_context_get_sink_info_by_name(c, info->default_sink_name,
                                                            SinkInfoCallback, userdata);
        if (op) {
            pa_operation_unref(op);
        }
    } else {
        std::cerr << "PulseAudioCapturer: No default sink found\n";
        pa_threaded_mainloop_signal(self->m_mainloop, 0);
    }
}

void PulseAudioCapturer::SinkInfoCallback(pa_context* c, const pa_sink_info* info, int eol, void* userdata) {
    auto* self = static_cast<PulseAudioCapturer*>(userdata);

    if (eol > 0) {
        // End of list
        pa_threaded_mainloop_signal(self->m_mainloop, 0);
        return;
    }

    if (info && info->monitor_source_name) {
        self->m_monitorSource = info->monitor_source_name;
        self->m_sourceSampleRate = info->sample_spec.rate;
        std::cerr << "PulseAudioCapturer: Monitor source: " << info->monitor_source_name
                  << " (sample rate: " << info->sample_spec.rate << " Hz)\n";
    }
}

void PulseAudioCapturer::StreamStateCallback(pa_stream* s, void* userdata) {
    auto* self = static_cast<PulseAudioCapturer*>(userdata);
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

void PulseAudioCapturer::StreamReadCallback(pa_stream* s, size_t length, void* userdata) {
    auto* self = static_cast<PulseAudioCapturer*>(userdata);

    if (!self->m_running) {
        return;
    }

    const void* data;
    size_t nbytes;

    // Peek at the data
    if (pa_stream_peek(s, &data, &nbytes) < 0) {
        std::cerr << "PulseAudioCapturer: Failed to peek stream data\n";
        return;
    }

    if (data && nbytes > 0) {
        self->ProcessAudio(data, nbytes);
    }

    // Drop the data
    pa_stream_drop(s);
}

void PulseAudioCapturer::ProcessAudio(const void* data, size_t length) {
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

uint64_t PulseAudioCapturer::GetTimestampMs() const {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return static_cast<uint64_t>(ts.tv_sec) * 1000 + ts.tv_nsec / 1000000;
}

}  // namespace snacka
