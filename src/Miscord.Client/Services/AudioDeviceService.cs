using System.Runtime.InteropServices;
using SIPSorcery.Media;
using SIPSorceryMedia.SDL2;
using SIPSorceryMedia.Abstractions;

namespace Miscord.Client.Services;

public interface IAudioDeviceService : IDisposable
{
    IReadOnlyList<string> GetInputDevices();
    IReadOnlyList<string> GetOutputDevices();

    bool IsTestingInput { get; }
    bool IsLoopbackEnabled { get; }

    Task StartInputTestAsync(string? deviceName, Action<float> onRmsUpdate);
    void SetLoopbackEnabled(bool enabled, string? outputDevice);
    Task StopTestAsync();
}

public class AudioDeviceService : IAudioDeviceService
{
    private SDL2AudioSource? _testAudioSource;
    private SDL2AudioEndPoint? _testAudioSink;
    private Action<float>? _onRmsUpdate;
    private bool _isLoopbackEnabled;
    private CancellationTokenSource? _testCts;
    private bool _sdlInitialized;

    public bool IsTestingInput => _testAudioSource != null;
    public bool IsLoopbackEnabled => _isLoopbackEnabled;

    public AudioDeviceService()
    {
        InitializeSdl();
    }

    private void InitializeSdl()
    {
        if (_sdlInitialized) return;
        _sdlInitialized = true;
    }

    public IReadOnlyList<string> GetInputDevices()
    {
        try
        {
            EnsureSdl2Loaded();
            Console.WriteLine("AudioDeviceService: Getting input devices...");
            var devices = SDL2Helper.GetAudioRecordingDevices();
            Console.WriteLine($"AudioDeviceService: Found {devices.Count} input devices");
            foreach (var device in devices)
            {
                Console.WriteLine($"  - Input device: {device}");
            }
            return devices;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AudioDeviceService: Failed to get input devices - {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"AudioDeviceService: Stack trace: {ex.StackTrace}");
            return Array.Empty<string>();
        }
    }

    private static bool _sdl2LibraryLoaded;
    private static readonly object _sdl2LoadLock = new();
    private static IntPtr _sdl2Handle;

    private static void EnsureSdl2Loaded()
    {
        if (_sdl2LibraryLoaded) return;

        lock (_sdl2LoadLock)
        {
            if (_sdl2LibraryLoaded) return;

            // Try to load SDL2 from common locations on macOS
            var possiblePaths = new[]
            {
                "/opt/homebrew/lib/libSDL2.dylib",      // Apple Silicon Homebrew
                "/usr/local/lib/libSDL2.dylib",         // Intel Homebrew
                "/usr/lib/libSDL2.dylib",               // System
                "libSDL2.dylib",                        // Current directory / PATH
                "SDL2"                                  // Let system find it
            };

            foreach (var path in possiblePaths)
            {
                try
                {
                    if (NativeLibrary.TryLoad(path, out _sdl2Handle))
                    {
                        Console.WriteLine($"AudioDeviceService: Loaded SDL2 from {path}");
                        _sdl2LibraryLoaded = true;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"AudioDeviceService: Failed to load SDL2 from {path}: {ex.Message}");
                }
            }

            Console.WriteLine("AudioDeviceService: Could not load SDL2 from any known location");
            _sdl2LibraryLoaded = true; // Mark as attempted even if failed
        }
    }

    public IReadOnlyList<string> GetOutputDevices()
    {
        try
        {
            EnsureSdl2Loaded();
            Console.WriteLine("AudioDeviceService: Getting output devices...");
            var devices = SDL2Helper.GetAudioPlaybackDevices();
            Console.WriteLine($"AudioDeviceService: Found {devices.Count} output devices");
            foreach (var device in devices)
            {
                Console.WriteLine($"  - Output device: {device}");
            }
            return devices;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AudioDeviceService: Failed to get output devices - {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"AudioDeviceService: Stack trace: {ex.StackTrace}");
            return Array.Empty<string>();
        }
    }

    public async Task StartInputTestAsync(string? deviceName, Action<float> onRmsUpdate)
    {
        // Stop any existing test
        await StopTestAsync();

        _onRmsUpdate = onRmsUpdate;
        _testCts = new CancellationTokenSource();

        try
        {
            var audioEncoder = new AudioEncoder();
            // Use device name or empty string for default
            _testAudioSource = new SDL2AudioSource(deviceName ?? string.Empty, audioEncoder);

            // Subscribe to raw audio samples for RMS calculation
            _testAudioSource.OnAudioSourceRawSample += OnTestAudioSample;

            await _testAudioSource.StartAudio();
            Console.WriteLine($"AudioDeviceService: Started input test on device: {deviceName ?? "(default)"}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AudioDeviceService: Failed to start input test - {ex.Message}");
            await StopTestAsync();
            throw;
        }
    }

    public void SetLoopbackEnabled(bool enabled, string? outputDevice)
    {
        _isLoopbackEnabled = enabled;

        if (enabled && _testAudioSource != null)
        {
            try
            {
                if (_testAudioSink == null)
                {
                    var audioEncoder = new AudioEncoder();
                    _testAudioSink = new SDL2AudioEndPoint(outputDevice ?? string.Empty, audioEncoder);

                    // Set same format as source
                    var formats = _testAudioSource.GetAudioSourceFormats();
                    if (formats.Count > 0)
                    {
                        _testAudioSink.SetAudioSinkFormat(formats[0]);
                    }

                    _ = _testAudioSink.StartAudioSink();
                    Console.WriteLine($"AudioDeviceService: Enabled loopback on device: {outputDevice ?? "(default)"}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AudioDeviceService: Failed to enable loopback - {ex.Message}");
                _isLoopbackEnabled = false;
            }
        }
        else if (!enabled && _testAudioSink != null)
        {
            try
            {
                _testAudioSink.CloseAudioSink();
            }
            catch { }
            _testAudioSink = null;
            Console.WriteLine("AudioDeviceService: Disabled loopback");
        }
    }

    public async Task StopTestAsync()
    {
        _testCts?.Cancel();
        _testCts?.Dispose();
        _testCts = null;

        if (_testAudioSource != null)
        {
            try
            {
                _testAudioSource.OnAudioSourceRawSample -= OnTestAudioSample;
                await _testAudioSource.CloseAudio();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AudioDeviceService: Error closing test audio source - {ex.Message}");
            }
            _testAudioSource = null;
        }

        if (_testAudioSink != null)
        {
            try
            {
                _ = _testAudioSink.CloseAudioSink();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AudioDeviceService: Error closing test audio sink - {ex.Message}");
            }
            _testAudioSink = null;
        }

        _onRmsUpdate = null;
        _isLoopbackEnabled = false;
        Console.WriteLine("AudioDeviceService: Stopped input test");
    }

    private void OnTestAudioSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
    {
        if (sample.Length == 0) return;

        // Calculate RMS
        double sumOfSquares = 0;
        for (int i = 0; i < sample.Length; i++)
        {
            sumOfSquares += sample[i] * sample[i];
        }
        double rms = Math.Sqrt(sumOfSquares / sample.Length);

        // Normalize to 0-1 range (short.MaxValue = 32767)
        float normalizedRms = (float)Math.Min(1.0, rms / 10000.0);

        _onRmsUpdate?.Invoke(normalizedRms);

        // If loopback is enabled, send audio to output
        if (_isLoopbackEnabled && _testAudioSink != null)
        {
            // Convert short[] to byte[] for the sink
            byte[] pcmBytes = new byte[sample.Length * 2];
            Buffer.BlockCopy(sample, 0, pcmBytes, 0, pcmBytes.Length);
            _testAudioSink.GotAudioSample(pcmBytes);
        }
    }

    public void Dispose()
    {
        StopTestAsync().GetAwaiter().GetResult();
    }
}
