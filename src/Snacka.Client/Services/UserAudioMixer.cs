using System.Collections.Concurrent;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.SDL2;
using Snacka.Client.Services.Audio;

namespace Snacka.Client.Services;

/// <summary>
/// Audio mixer that supports per-user volume control.
/// Decodes incoming audio (Opus, G.711), applies per-user volume, and sends to audio output.
/// Supports high-quality Opus at 48kHz and legacy G.711 at 8kHz.
/// </summary>
public interface IUserAudioMixer : IAsyncDisposable
{
    /// <summary>
    /// Processes an incoming audio RTP packet, applying per-user volume.
    /// </summary>
    void ProcessAudioPacket(uint ssrc, Guid? userId, ushort seqNum, uint timestamp,
                            int payloadType, bool marker, byte[] payload);

    /// <summary>
    /// Sets the volume for a specific user (0.0 - 2.0, where 1.0 is normal).
    /// </summary>
    void SetUserVolume(Guid userId, float volume);

    /// <summary>
    /// Gets the volume for a specific user.
    /// </summary>
    float GetUserVolume(Guid userId);

    /// <summary>
    /// Sets the master output volume (0.0 - 2.0).
    /// </summary>
    void SetMasterVolume(float volume);

    /// <summary>
    /// Gets the master output volume.
    /// </summary>
    float GetMasterVolume();

    /// <summary>
    /// Starts the audio mixer with the specified output device.
    /// </summary>
    Task StartAsync(string? outputDevice = null);

    /// <summary>
    /// Stops the audio mixer.
    /// </summary>
    Task StopAsync();
}

public class UserAudioMixer : IUserAudioMixer
{
    private readonly ConcurrentDictionary<Guid, float> _userVolumes = new();
    private readonly AudioEncoder _audioEncoder = new(includeOpus: true);  // Enable Opus support for mic audio
    private SDL2AudioEndPoint? _audioOutput;
    private float _masterVolume = 1.0f;
    private const float DefaultVolume = 1.0f;
    private const float MaxVolume = 3.0f; // 300% - matches input gain range

    // Separate encoder and output for screen audio to avoid Opus decoder state corruption
    // The Opus codec is stateful - sharing a decoder between mic and screen audio corrupts both
    private readonly AudioEncoder _screenAudioEncoder = new(includeOpus: true);
    private SDL2AudioEndPoint? _screenAudioOutput;
    private int _screenAudioRecvCount;  // Diagnostic counter

    // G.711 mu-law decode table (256 entries)
    private static readonly short[] MuLawDecodeTable = GenerateMuLawDecodeTable();

    // G.711 a-law decode table (256 entries)
    private static readonly short[] ALawDecodeTable = GenerateALawDecodeTable();

    // Opus format for high-quality 48kHz audio
    private AudioFormat? _currentFormat;

    // Audio processor for AEC reference (feeds playback audio to AEC)
    private IAudioProcessor? _audioProcessor;
    private int _aecFeedCount;

    public async Task StartAsync(string? outputDevice = null)
    {
        if (_audioOutput != null)
        {
            await StopAsync();
        }

        try
        {
            // Get Opus format FIRST (48kHz) - must set format BEFORE starting audio sink
            // so SDL2 opens the device at the correct sample rate
            var supportedFormats = _audioEncoder.SupportedFormats;
            var opusFormat = supportedFormats.FirstOrDefault(f => f.FormatName == "OPUS");
            var pcmuFormat = supportedFormats.FirstOrDefault(f => f.FormatName == "PCMU");

            // AudioFormat is a struct, so check FormatName to see if it's valid
            if (!string.IsNullOrEmpty(opusFormat.FormatName))
            {
                _currentFormat = opusFormat;
            }
            else if (!string.IsNullOrEmpty(pcmuFormat.FormatName))
            {
                _currentFormat = pcmuFormat;
            }

            // Initialize mic audio output - set format BEFORE starting
            _audioOutput = new SDL2AudioEndPoint(outputDevice ?? string.Empty, _audioEncoder);
            if (_currentFormat.HasValue)
            {
                _audioOutput.SetAudioSinkFormat(_currentFormat.Value);
            }
            await _audioOutput.StartAudioSink();

            // Initialize separate screen audio output with its own encoder/decoder
            // This prevents Opus decoder state corruption between mic and screen audio
            _screenAudioOutput = new SDL2AudioEndPoint(outputDevice ?? string.Empty, _screenAudioEncoder);
            if (_currentFormat.HasValue)
            {
                _screenAudioOutput.SetAudioSinkFormat(_currentFormat.Value);
            }
            await _screenAudioOutput.StartAudioSink();

            if (_currentFormat.HasValue)
            {
                Console.WriteLine($"UserAudioMixer: Started mic audio output '{outputDevice ?? "(default)"}', format: {_currentFormat.Value.FormatName} ({_currentFormat.Value.ClockRate}Hz, {_currentFormat.Value.ChannelCount} ch)");
                Console.WriteLine($"UserAudioMixer: Started screen audio output (separate decoder) with same format");

                // Verify we're using 48kHz for Opus
                if (_currentFormat.Value.FormatName == "OPUS" && _currentFormat.Value.ClockRate != 48000)
                {
                    Console.WriteLine($"UserAudioMixer: WARNING - Opus should use 48kHz but format is {_currentFormat.Value.ClockRate}Hz!");
                }
            }
            else
            {
                Console.WriteLine($"UserAudioMixer: Started with output device '{outputDevice ?? "(default)"}', no format set");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"UserAudioMixer: Failed to start - {ex.Message}");
            throw;
        }
    }

    // Opus payload types
    private const int OpusMicPayloadType = 111;     // Microphone audio
    private const int OpusScreenPayloadType = 112;  // Screen share audio

    private int _micAudioRecvCount;

    public void ProcessAudioPacket(uint ssrc, Guid? userId, ushort seqNum, uint timestamp,
                                   int payloadType, bool marker, byte[] payload)
    {
        if (_audioOutput == null)
        {
            Console.WriteLine("UserAudioMixer: _audioOutput is NULL, cannot play audio!");
            return;
        }

        // Screen audio (PT 112) and mic audio (PT 111) both use Opus codec
        // Remap PT 112 to PT 111 so the audio sink can decode it
        var outputPayloadType = payloadType == OpusScreenPayloadType ? OpusMicPayloadType : payloadType;

        // Diagnostic: Log first few mic audio packets and unexpected payload types
        if (payloadType != OpusScreenPayloadType)
        {
            _micAudioRecvCount++;
            if (_micAudioRecvCount <= 5)
            {
                Console.WriteLine($"UserAudioMixer: Mic audio #{_micAudioRecvCount}, PT={payloadType}, size={payload.Length}, currentFormat={_currentFormat?.FormatName ?? "none"}");
            }
            // Warn if payload type doesn't match expected format
            if (payloadType != OpusMicPayloadType && payloadType != 0 && payloadType != 8)
            {
                if (_micAudioRecvCount <= 10)
                {
                    Console.WriteLine($"UserAudioMixer: WARNING - Unexpected payload type {payloadType} (expected 111=Opus, 0=PCMU, 8=PCMA)");
                }
            }
        }

        // Screen audio uses a completely separate audio output with its own Opus decoder
        // This prevents decoder state corruption when mic and screen audio are interleaved
        if (payloadType == OpusScreenPayloadType)
        {
            if (_screenAudioOutput == null)
            {
                Console.WriteLine("UserAudioMixer: Screen audio output is NULL!");
                return;
            }

            // Diagnostic: Log first few screen audio packets received
            _screenAudioRecvCount++;
            if (_screenAudioRecvCount <= 5)
            {
                Console.WriteLine($"UserAudioMixer: Screen audio #{_screenAudioRecvCount}, size={payload.Length}, ssrc={ssrc}, ts={timestamp}, seq={seqNum}");
            }

            _screenAudioOutput.GotAudioRtp(null!, ssrc, seqNum, timestamp, outputPayloadType, marker, payload);
            return;
        }

        // Determine volume to apply
        float volume = _masterVolume;
        if (userId.HasValue)
        {
            volume *= GetUserVolume(userId.Value);
        }

        // Feed audio to AEC processor if present (needs decoded PCM)
        if (_audioProcessor != null && _currentFormat.HasValue)
        {
            // Always decode for AEC reference
            var pcmSamples = DecodePayloadToPcm(payload, payloadType);
            if (pcmSamples != null && pcmSamples.Length > 0)
            {
                _aecFeedCount++;
                if (_aecFeedCount <= 5)
                {
                    Console.WriteLine($"UserAudioMixer: Feeding AEC #{_aecFeedCount}: {pcmSamples.Length} samples @ {_currentFormat.Value.ClockRate}Hz/{_currentFormat.Value.ChannelCount}ch");
                }
                _audioProcessor.FeedPlaybackAudio(pcmSamples, _currentFormat.Value.ClockRate, _currentFormat.Value.ChannelCount);
            }
            else if (_aecFeedCount <= 5)
            {
                Console.WriteLine($"UserAudioMixer: Failed to decode payload for AEC (PT={payloadType}, size={payload.Length})");
            }
        }

        // Fast path: if volume is 1.0, no processing needed
        if (Math.Abs(volume - 1.0f) < 0.001f)
        {
            // Pass through directly (null endpoint is OK for audio playback)
            _audioOutput.GotAudioRtp(null!, ssrc, seqNum, timestamp, outputPayloadType, marker, payload);
            return;
        }

        // Apply volume by decoding, scaling, and re-encoding
        var processedPayload = ApplyVolumeToPayload(payload, payloadType, volume);
        _audioOutput.GotAudioRtp(null!, ssrc, seqNum, timestamp, outputPayloadType, marker, processedPayload);
    }

    /// <summary>
    /// Decodes audio payload to PCM samples without any volume adjustment.
    /// Used for feeding audio to AEC processor.
    /// </summary>
    private short[]? DecodePayloadToPcm(byte[] payload, int payloadType)
    {
        bool isG711 = payloadType == 0 || payloadType == 8;

        if (isG711)
        {
            // G.711: Decode to PCM
            var samples = new short[payload.Length];
            bool isMuLaw = payloadType == 0;
            var decodeTable = isMuLaw ? MuLawDecodeTable : ALawDecodeTable;

            for (int i = 0; i < payload.Length; i++)
            {
                samples[i] = decodeTable[payload[i]];
            }
            return samples;
        }
        else
        {
            // Opus: Use AudioEncoder to decode
            if (!_currentFormat.HasValue)
            {
                return null;
            }

            try
            {
                return _audioEncoder.DecodeAudio(payload, _currentFormat.Value);
            }
            catch
            {
                return null;
            }
        }
    }

    private byte[] ApplyVolumeToPayload(byte[] payload, int payloadType, float volume)
    {
        // Payload type 0 = PCMU (mu-law), 8 = PCMA (a-law)
        // Opus payload types: 111 = microphone audio, 112 = screen audio (both use Opus codec)
        bool isG711 = payloadType == 0 || payloadType == 8;

        if (isG711)
        {
            // G.711: Use fast table-based decode/encode
            var result = new byte[payload.Length];
            bool isMuLaw = payloadType == 0;
            var decodeTable = isMuLaw ? MuLawDecodeTable : ALawDecodeTable;

            for (int i = 0; i < payload.Length; i++)
            {
                // Decode to linear PCM
                short sample = decodeTable[payload[i]];

                // Apply volume with clamping
                float scaled = sample * volume;
                short clamped = (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);

                // Encode back
                result[i] = isMuLaw ? MuLawEncode(clamped) : ALawEncode(clamped);
            }

            return result;
        }
        else
        {
            // Opus or other codec: Use AudioEncoder to decode/encode
            // Use the current format (should be Opus if configured)
            if (!_currentFormat.HasValue)
            {
                // No format configured, pass through unchanged
                return payload;
            }
            var format = _currentFormat.Value;

            try
            {
                // Decode to PCM
                var pcmSamples = _audioEncoder.DecodeAudio(payload, format);
                if (pcmSamples == null || pcmSamples.Length == 0)
                {
                    return payload; // Decoding failed, pass through unchanged
                }

                // Apply volume to PCM samples
                for (int i = 0; i < pcmSamples.Length; i++)
                {
                    float scaled = pcmSamples[i] * volume;
                    pcmSamples[i] = (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
                }

                // Encode back to Opus
                var encoded = _audioEncoder.EncodeAudio(pcmSamples, format);
                return encoded;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UserAudioMixer: Failed to apply volume to Opus packet: {ex.Message}");
                return payload; // Return unchanged on error
            }
        }
    }

    public void SetUserVolume(Guid userId, float volume)
    {
        var clamped = Math.Clamp(volume, 0f, MaxVolume);
        _userVolumes[userId] = clamped;
        Console.WriteLine($"UserAudioMixer: Set volume for user {userId} to {clamped:P0}");
    }

    public float GetUserVolume(Guid userId)
    {
        return _userVolumes.GetValueOrDefault(userId, DefaultVolume);
    }

    public void SetMasterVolume(float volume)
    {
        _masterVolume = Math.Clamp(volume, 0f, MaxVolume);
        Console.WriteLine($"UserAudioMixer: Set master volume to {_masterVolume:P0}");
    }

    public float GetMasterVolume() => _masterVolume;

    /// <summary>
    /// Sets the audio processor for AEC reference.
    /// Playback audio will be fed to this processor.
    /// </summary>
    public void SetAudioProcessor(IAudioProcessor? processor)
    {
        _audioProcessor = processor;
        Console.WriteLine($"UserAudioMixer: Audio processor {(processor != null ? "set" : "cleared")}");
    }

    public async Task StopAsync()
    {
        if (_audioOutput != null)
        {
            try
            {
                await _audioOutput.CloseAudioSink();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UserAudioMixer: Error stopping mic audio - {ex.Message}");
            }
            _audioOutput = null;
        }

        if (_screenAudioOutput != null)
        {
            try
            {
                await _screenAudioOutput.CloseAudioSink();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UserAudioMixer: Error stopping screen audio - {ex.Message}");
            }
            _screenAudioOutput = null;
        }

        Console.WriteLine("UserAudioMixer: Stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    #region G.711 Codec Implementation

    /// <summary>
    /// Generates the mu-law decode lookup table.
    /// </summary>
    private static short[] GenerateMuLawDecodeTable()
    {
        var table = new short[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = MuLawDecodeValue((byte)i);
        }
        return table;
    }

    /// <summary>
    /// Generates the a-law decode lookup table.
    /// </summary>
    private static short[] GenerateALawDecodeTable()
    {
        var table = new short[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = ALawDecodeValue((byte)i);
        }
        return table;
    }

    /// <summary>
    /// Decodes a single mu-law byte to linear PCM16.
    /// </summary>
    private static short MuLawDecodeValue(byte mulaw)
    {
        // Invert all bits
        mulaw = (byte)~mulaw;

        // Extract sign, exponent, and mantissa
        int sign = (mulaw & 0x80) != 0 ? -1 : 1;
        int exponent = (mulaw >> 4) & 0x07;
        int mantissa = mulaw & 0x0F;

        // Decode
        int sample = ((mantissa << 3) + 0x84) << exponent;
        sample -= 0x84;

        return (short)(sign * sample);
    }

    /// <summary>
    /// Encodes a linear PCM16 sample to mu-law.
    /// </summary>
    private static byte MuLawEncode(short sample)
    {
        const int BIAS = 0x84;
        const int MAX = 0x7FFF;

        // Get sign bit
        int sign = (sample >> 8) & 0x80;
        if (sign != 0)
        {
            sample = (short)-sample;
        }

        // Clip to max
        if (sample > MAX)
        {
            sample = MAX;
        }

        // Add bias
        sample += BIAS;

        // Find segment (exponent)
        int exponent = 7;
        for (int expMask = 0x4000; (sample & expMask) == 0 && exponent > 0; exponent--, expMask >>= 1) { }

        // Get mantissa
        int mantissa = (sample >> (exponent + 3)) & 0x0F;

        // Combine and invert
        byte uval = (byte)(sign | (exponent << 4) | mantissa);
        return (byte)~uval;
    }

    /// <summary>
    /// Decodes a single a-law byte to linear PCM16.
    /// </summary>
    private static short ALawDecodeValue(byte alaw)
    {
        // Toggle every other bit
        alaw ^= 0x55;

        // Extract sign, exponent, and mantissa
        int sign = (alaw & 0x80) != 0 ? -1 : 1;
        int exponent = (alaw >> 4) & 0x07;
        int mantissa = alaw & 0x0F;

        int sample;
        if (exponent == 0)
        {
            sample = (mantissa << 4) + 8;
        }
        else
        {
            sample = ((mantissa << 4) + 0x108) << (exponent - 1);
        }

        return (short)(sign * sample);
    }

    /// <summary>
    /// Encodes a linear PCM16 sample to a-law.
    /// </summary>
    private static byte ALawEncode(short sample)
    {
        const int MAX = 0x7FFF;

        // Get sign bit
        int sign = 0;
        if (sample < 0)
        {
            sign = 0x80;
            sample = (short)-sample;
        }

        // Clip to max
        if (sample > MAX)
        {
            sample = MAX;
        }

        int exponent = 7;
        int expMask = 0x4000;

        // Find segment
        for (; (sample & expMask) == 0 && exponent > 0; exponent--, expMask >>= 1) { }

        int mantissa;
        if (exponent == 0)
        {
            mantissa = (sample >> 4) & 0x0F;
        }
        else
        {
            mantissa = (sample >> (exponent + 3)) & 0x0F;
        }

        // Combine and toggle bits
        byte aval = (byte)(sign | (exponent << 4) | mantissa);
        return (byte)(aval ^ 0x55);
    }

    #endregion
}
