using SoundFlow.Extensions.WebRtc.Apm;

namespace Snacka.Client.Services.Audio;

/// <summary>
/// Wrapper around WebRTC Audio Processing Module for AEC, noise suppression, and AGC.
/// This processor works with our existing SDL2-based audio pipeline.
/// </summary>
public class WebRtcAudioProcessor : IAudioProcessor
{
    private readonly AudioProcessingModule _apm;
    private readonly StreamConfig _inputConfig;
    private readonly StreamConfig _outputConfig;
    private readonly StreamConfig _reverseConfig;

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _frameSize;

    // Buffers for processing (WebRTC APM needs float arrays per channel)
    private readonly float[][] _inputBuffer;
    private readonly float[][] _outputBuffer;
    private readonly float[][] _reverseBuffer;

    // Circular buffers for accumulating samples until we have a full frame
    private readonly float[] _inputAccumulator;
    private readonly float[] _outputAccumulator;
    private int _inputAccumulatorPos;
    private int _outputAccumulatorPos;

    private bool _disposed;

    // Feature flags
    private readonly bool _aecEnabled;
    private readonly bool _nsEnabled;
    private readonly bool _agcEnabled;
    private long _playbackSamplesReceived;
    private long _captureSamplesProcessed;

    // Diagnostic logging
    private int _playbackLogCount;
    private int _captureLogCount;
    private int _channelConvertLogCount;
    private const int MaxLogCount = 3;

    // IAudioProcessor properties
    public bool IsAecActive => _aecEnabled && _playbackSamplesReceived > 0;
    public bool IsNoiseSuppressionEnabled => _nsEnabled;
    public bool IsAgcEnabled => _agcEnabled;

    /// <summary>
    /// Creates a new WebRTC audio processor.
    /// </summary>
    /// <param name="sampleRate">Sample rate (must be 8000, 16000, 32000, or 48000 Hz)</param>
    /// <param name="channels">Number of channels (1 or 2)</param>
    /// <param name="enableAec">Enable acoustic echo cancellation</param>
    /// <param name="enableNoiseSuppression">Enable noise suppression</param>
    /// <param name="noiseSuppressionLevel">Noise suppression aggressiveness</param>
    /// <param name="enableAgc">Enable automatic gain control</param>
    public WebRtcAudioProcessor(
        int sampleRate = 48000,
        int channels = 2,
        bool enableAec = true,
        bool enableNoiseSuppression = true,
        NoiseSuppressionLevel noiseSuppressionLevel = NoiseSuppressionLevel.High,
        bool enableAgc = true)
    {
        // Validate sample rate
        if (sampleRate != 8000 && sampleRate != 16000 && sampleRate != 32000 && sampleRate != 48000)
        {
            throw new ArgumentException("Sample rate must be 8000, 16000, 32000, or 48000 Hz", nameof(sampleRate));
        }

        _sampleRate = sampleRate;
        _channels = channels;
        _aecEnabled = enableAec;
        _nsEnabled = enableNoiseSuppression;
        _agcEnabled = enableAgc;

        // WebRTC APM processes 10ms frames
        _frameSize = sampleRate / 100; // 10ms worth of samples per channel

        // Create APM instance
        _apm = new AudioProcessingModule();

        // Create stream configs
        _inputConfig = new StreamConfig(sampleRate, channels);
        _outputConfig = new StreamConfig(sampleRate, channels);
        _reverseConfig = new StreamConfig(sampleRate, channels);

        // Configure APM
        using var config = new ApmConfig();

        if (enableAec)
        {
            config.SetEchoCanceller(enabled: true, mobileMode: false);
        }

        if (enableNoiseSuppression)
        {
            config.SetNoiseSuppression(enabled: true, level: noiseSuppressionLevel);
        }

        if (enableAgc)
        {
            // Use AGC2 (newer, better quality)
            config.SetGainController2(enabled: true);
        }

        // Enable high-pass filter to remove DC offset and low rumble
        config.SetHighPassFilter(enabled: true);

        // Apply configuration
        var result = _apm.ApplyConfig(config);
        if (result != ApmError.NoError)
        {
            throw new InvalidOperationException($"Failed to apply APM config: {result}");
        }

        // Initialize APM
        result = _apm.Initialize();
        if (result != ApmError.NoError)
        {
            throw new InvalidOperationException($"Failed to initialize APM: {result}");
        }

        // Allocate buffers (one array per channel)
        _inputBuffer = new float[channels][];
        _outputBuffer = new float[channels][];
        _reverseBuffer = new float[channels][];

        for (int i = 0; i < channels; i++)
        {
            _inputBuffer[i] = new float[_frameSize];
            _outputBuffer[i] = new float[_frameSize];
            _reverseBuffer[i] = new float[_frameSize];
        }

        // Accumulators for interleaved audio
        _inputAccumulator = new float[_frameSize * channels];
        _outputAccumulator = new float[_frameSize * channels];

        Console.WriteLine($"WebRtcAudioProcessor: Initialized ({sampleRate}Hz, {channels}ch, frameSize={_frameSize})");
        Console.WriteLine($"WebRtcAudioProcessor: AEC={enableAec}, NS={enableNoiseSuppression} ({noiseSuppressionLevel}), AGC={enableAgc}");
    }

    /// <summary>
    /// Feed playback audio (far-end) for echo cancellation reference.
    /// Call this with the audio being played to speakers.
    /// </summary>
    /// <param name="samples">Interleaved float samples</param>
    public void FeedPlaybackAudio(ReadOnlySpan<float> samples)
    {
        if (_disposed) return;

        // Process in frame-sized chunks
        int pos = 0;
        while (pos + _frameSize * _channels <= samples.Length)
        {
            // Deinterleave into per-channel buffers
            for (int i = 0; i < _frameSize; i++)
            {
                for (int ch = 0; ch < _channels; ch++)
                {
                    _reverseBuffer[ch][i] = samples[pos + i * _channels + ch];
                }
            }

            // Feed to APM as reverse stream (for AEC reference)
            _apm.AnalyzeReverseStream(_reverseBuffer, _reverseConfig);

            pos += _frameSize * _channels;
        }
    }

    /// <summary>
    /// Feed playback audio from Int16 PCM samples.
    /// </summary>
    public void FeedPlaybackAudio(ReadOnlySpan<short> samples)
    {
        if (_disposed) return;

        _playbackSamplesReceived += samples.Length;

        // Log first few playback feeds
        if (_playbackLogCount < MaxLogCount)
        {
            _playbackLogCount++;
            Console.WriteLine($"WebRtcAudioProcessor: Fed playback #{_playbackLogCount}: {samples.Length} samples, total={_playbackSamplesReceived}");
        }

        // Process in frame-sized chunks
        int pos = 0;
        while (pos + _frameSize * _channels <= samples.Length)
        {
            // Deinterleave and convert to float
            for (int i = 0; i < _frameSize; i++)
            {
                for (int ch = 0; ch < _channels; ch++)
                {
                    _reverseBuffer[ch][i] = samples[pos + i * _channels + ch] / 32768f;
                }
            }

            // Feed to APM as reverse stream
            _apm.AnalyzeReverseStream(_reverseBuffer, _reverseConfig);

            pos += _frameSize * _channels;
        }
    }

    // Buffers for mono-to-stereo conversion
    private short[]? _monoToStereoBuffer;
    private short[]? _stereoToMonoBuffer;

    /// <summary>
    /// IAudioProcessor interface: Feed playback audio for echo cancellation reference.
    /// </summary>
    public void FeedPlaybackAudio(ReadOnlySpan<short> samples, int sampleRate, int channels)
    {
        if (_disposed) return;

        // Sample rate must match (no resampling implemented)
        if (sampleRate != _sampleRate)
        {
            // Console.WriteLine($"WebRtcAudioProcessor: Playback sample rate mismatch ({sampleRate}Hz vs {_sampleRate}Hz)");
            return;
        }

        // If channels match, use direct path
        if (channels == _channels)
        {
            FeedPlaybackAudio(samples);
            return;
        }

        // Convert mono to stereo or stereo to mono
        if (channels == 1 && _channels == 2)
        {
            // Mono to stereo: duplicate each sample
            int stereoLength = samples.Length * 2;
            if (_monoToStereoBuffer == null || _monoToStereoBuffer.Length < stereoLength)
            {
                _monoToStereoBuffer = new short[stereoLength];
            }

            for (int i = 0; i < samples.Length; i++)
            {
                _monoToStereoBuffer[i * 2] = samples[i];
                _monoToStereoBuffer[i * 2 + 1] = samples[i];
            }

            FeedPlaybackAudio(_monoToStereoBuffer.AsSpan(0, stereoLength));
        }
        else if (channels == 2 && _channels == 1)
        {
            // Stereo to mono: average channels
            int monoLength = samples.Length / 2;
            if (_stereoToMonoBuffer == null || _stereoToMonoBuffer.Length < monoLength)
            {
                _stereoToMonoBuffer = new short[monoLength];
            }

            for (int i = 0; i < monoLength; i++)
            {
                _stereoToMonoBuffer[i] = (short)((samples[i * 2] + samples[i * 2 + 1]) / 2);
            }

            FeedPlaybackAudio(_stereoToMonoBuffer.AsSpan(0, monoLength));
        }
    }

    // Buffers for capture channel conversion
    private short[]? _captureMonoToStereoBuffer;
    private short[]? _captureStereoToMonoBuffer;
    private short[]? _captureOutputStereoBuffer;

    /// <summary>
    /// IAudioProcessor interface: Process capture audio.
    /// </summary>
    public int ProcessCaptureAudio(ReadOnlySpan<short> input, Span<short> output, int sampleRate, int channels)
    {
        if (_disposed) return 0;

        _captureSamplesProcessed += input.Length;

        // Log first few capture processes
        if (_captureLogCount < MaxLogCount)
        {
            _captureLogCount++;
            Console.WriteLine($"WebRtcAudioProcessor: Processing capture #{_captureLogCount}: {input.Length} samples @ {sampleRate}Hz/{channels}ch, playback received={_playbackSamplesReceived}, AEC active={IsAecActive}");
        }

        // Sample rate must match (no resampling implemented)
        if (sampleRate != _sampleRate)
        {
            if (_captureLogCount <= MaxLogCount)
            {
                Console.WriteLine($"WebRtcAudioProcessor: Sample rate mismatch! Input={sampleRate}Hz, processor={_sampleRate}Hz - passing through");
            }
            input.CopyTo(output);
            return input.Length;
        }

        // If channels match, use direct path
        if (channels == _channels)
        {
            return ProcessMicrophoneAudio(input, output);
        }

        // Log channel conversion once
        if (_channelConvertLogCount == 0)
        {
            _channelConvertLogCount++;
            Console.WriteLine($"WebRtcAudioProcessor: Converting {channels}ch -> {_channels}ch for processing");
        }

        // Handle mono input with stereo processor
        if (channels == 1 && _channels == 2)
        {
            // Convert mono to stereo for processing
            int stereoLength = input.Length * 2;
            if (_captureMonoToStereoBuffer == null || _captureMonoToStereoBuffer.Length < stereoLength)
            {
                _captureMonoToStereoBuffer = new short[stereoLength];
            }
            if (_captureOutputStereoBuffer == null || _captureOutputStereoBuffer.Length < stereoLength)
            {
                _captureOutputStereoBuffer = new short[stereoLength];
            }

            for (int i = 0; i < input.Length; i++)
            {
                _captureMonoToStereoBuffer[i * 2] = input[i];
                _captureMonoToStereoBuffer[i * 2 + 1] = input[i];
            }

            // Process stereo
            int processedStereo = ProcessMicrophoneAudio(
                _captureMonoToStereoBuffer.AsSpan(0, stereoLength),
                _captureOutputStereoBuffer.AsSpan(0, stereoLength));

            // Convert back to mono
            int monoLength = processedStereo / 2;
            for (int i = 0; i < monoLength; i++)
            {
                output[i] = (short)((_captureOutputStereoBuffer[i * 2] + _captureOutputStereoBuffer[i * 2 + 1]) / 2);
            }

            return monoLength;
        }

        // Handle stereo input with mono processor
        if (channels == 2 && _channels == 1)
        {
            // Convert stereo to mono for processing
            int monoLength = input.Length / 2;
            if (_captureStereoToMonoBuffer == null || _captureStereoToMonoBuffer.Length < monoLength)
            {
                _captureStereoToMonoBuffer = new short[monoLength];
            }

            for (int i = 0; i < monoLength; i++)
            {
                _captureStereoToMonoBuffer[i] = (short)((input[i * 2] + input[i * 2 + 1]) / 2);
            }

            // Process mono
            Span<short> monoOutput = stackalloc short[monoLength];
            int processedMono = ProcessMicrophoneAudio(
                _captureStereoToMonoBuffer.AsSpan(0, monoLength),
                monoOutput);

            // Convert back to stereo
            for (int i = 0; i < processedMono; i++)
            {
                output[i * 2] = monoOutput[i];
                output[i * 2 + 1] = monoOutput[i];
            }

            return processedMono * 2;
        }

        // Fallback: pass through unchanged
        input.CopyTo(output);
        return input.Length;
    }

    /// <summary>
    /// Process microphone audio (near-end) through the APM.
    /// Returns processed audio with AEC, noise suppression, and AGC applied.
    /// </summary>
    /// <param name="input">Input interleaved float samples</param>
    /// <param name="output">Output buffer for processed samples (same size as input)</param>
    /// <returns>Number of samples written to output</returns>
    public int ProcessMicrophoneAudio(ReadOnlySpan<float> input, Span<float> output)
    {
        if (_disposed) return 0;

        int outputPos = 0;

        // First, flush any accumulated output from previous calls
        while (_outputAccumulatorPos > 0 && outputPos < output.Length)
        {
            int toCopy = Math.Min(_outputAccumulatorPos, output.Length - outputPos);
            _outputAccumulator.AsSpan(0, toCopy).CopyTo(output.Slice(outputPos, toCopy));
            outputPos += toCopy;

            // Shift remaining data
            if (toCopy < _outputAccumulatorPos)
            {
                Array.Copy(_outputAccumulator, toCopy, _outputAccumulator, 0, _outputAccumulatorPos - toCopy);
            }
            _outputAccumulatorPos -= toCopy;
        }

        // Add input to accumulator
        int inputPos = 0;
        while (inputPos < input.Length)
        {
            int toAdd = Math.Min(input.Length - inputPos, _inputAccumulator.Length - _inputAccumulatorPos);
            input.Slice(inputPos, toAdd).CopyTo(_inputAccumulator.AsSpan(_inputAccumulatorPos, toAdd));
            _inputAccumulatorPos += toAdd;
            inputPos += toAdd;

            // Process when we have a full frame
            if (_inputAccumulatorPos >= _frameSize * _channels)
            {
                ProcessFrame();
                _inputAccumulatorPos = 0;

                // Copy output to result buffer
                int outputToCopy = Math.Min(_frameSize * _channels, output.Length - outputPos);
                _outputAccumulator.AsSpan(0, outputToCopy).CopyTo(output.Slice(outputPos, outputToCopy));
                outputPos += outputToCopy;

                // Store remainder in output accumulator
                if (outputToCopy < _frameSize * _channels)
                {
                    int remaining = _frameSize * _channels - outputToCopy;
                    Array.Copy(_outputAccumulator, outputToCopy, _outputAccumulator, 0, remaining);
                    _outputAccumulatorPos = remaining;
                }
            }
        }

        return outputPos;
    }

    /// <summary>
    /// Process microphone audio from Int16 PCM samples.
    /// </summary>
    public int ProcessMicrophoneAudio(ReadOnlySpan<short> input, Span<short> output)
    {
        if (_disposed) return 0;

        // Convert to float, process, convert back
        Span<float> floatInput = stackalloc float[input.Length];
        Span<float> floatOutput = stackalloc float[output.Length];

        for (int i = 0; i < input.Length; i++)
        {
            floatInput[i] = input[i] / 32768f;
        }

        int processed = ProcessMicrophoneAudio(floatInput, floatOutput);

        for (int i = 0; i < processed; i++)
        {
            output[i] = (short)Math.Clamp(floatOutput[i] * 32768f, -32768, 32767);
        }

        return processed;
    }

    private void ProcessFrame()
    {
        // Deinterleave input
        for (int i = 0; i < _frameSize; i++)
        {
            for (int ch = 0; ch < _channels; ch++)
            {
                _inputBuffer[ch][i] = _inputAccumulator[i * _channels + ch];
            }
        }

        // Process through APM
        var result = _apm.ProcessStream(_inputBuffer, _inputConfig, _outputConfig, _outputBuffer);
        if (result != ApmError.NoError)
        {
            Console.WriteLine($"WebRtcAudioProcessor: ProcessStream error: {result}");
        }

        // Interleave output
        for (int i = 0; i < _frameSize; i++)
        {
            for (int ch = 0; ch < _channels; ch++)
            {
                _outputAccumulator[i * _channels + ch] = _outputBuffer[ch][i];
            }
        }
    }

    /// <summary>
    /// Set the estimated delay between playback and capture in milliseconds.
    /// This helps the AEC align the reference signal properly.
    /// </summary>
    public void SetStreamDelay(int delayMs)
    {
        if (_disposed) return;
        _apm.SetStreamDelayMs(delayMs);
    }

    /// <summary>
    /// Get the current stream delay setting.
    /// </summary>
    public int GetStreamDelay()
    {
        if (_disposed) return 0;
        return _apm.GetStreamDelayMs();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _apm.Dispose();
        _inputConfig.Dispose();
        _outputConfig.Dispose();
        _reverseConfig.Dispose();

        Console.WriteLine("WebRtcAudioProcessor: Disposed");
    }
}
