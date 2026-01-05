using System.Reflection;
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
    // SDL2 P/Invoke declarations
    [DllImport("SDL2")]
    private static extern int SDL_Init(uint flags);

    [DllImport("SDL2")]
    private static extern void SDL_Quit();

    private const uint SDL_INIT_AUDIO = 0x00000010;

    private SDL2AudioSource? _testAudioSource;
    private SDL2AudioEndPoint? _testAudioSink;
    private Action<float>? _onRmsUpdate;
    private bool _isLoopbackEnabled;
    private CancellationTokenSource? _testCts;

    public bool IsTestingInput => _testAudioSource != null;
    public bool IsLoopbackEnabled => _isLoopbackEnabled;

    public AudioDeviceService()
    {
        EnsureSdl2Initialized();
    }

    public IReadOnlyList<string> GetInputDevices()
    {
        try
        {
            EnsureSdl2Initialized();
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
            return Array.Empty<string>();
        }
    }

    private static bool _sdl2Initialized;
    private static readonly object _sdl2InitLock = new();

    // SDL2 library paths to try on macOS
    private static readonly string[] Sdl2Paths =
    {
        "/opt/homebrew/lib/libSDL2.dylib",      // Apple Silicon Homebrew
        "/usr/local/lib/libSDL2.dylib",         // Intel Homebrew
        "/usr/lib/libSDL2.dylib",               // System
        "libSDL2.dylib",                        // Current directory / PATH
        "SDL2"                                  // Let system find it
    };

    private static IntPtr ResolveSdl2(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == "SDL2" || libraryName == "libSDL2" || libraryName == "SDL2.dll")
        {
            foreach (var path in Sdl2Paths)
            {
                if (NativeLibrary.TryLoad(path, out var handle))
                {
                    return handle;
                }
            }
        }
        return IntPtr.Zero;
    }

    private static void EnsureSdl2Initialized()
    {
        if (_sdl2Initialized) return;

        lock (_sdl2InitLock)
        {
            if (_sdl2Initialized) return;

            try
            {
                // Register DllImportResolver for the SDL2 assemblies
                var sdl2HelperAssembly = typeof(SDL2Helper).Assembly;
                NativeLibrary.SetDllImportResolver(sdl2HelperAssembly, ResolveSdl2);
                Console.WriteLine($"AudioDeviceService: Registered DllImportResolver for {sdl2HelperAssembly.GetName().Name}");

                // Also register for this assembly (for our local SDL_Init call)
                NativeLibrary.SetDllImportResolver(typeof(AudioDeviceService).Assembly, ResolveSdl2);
                Console.WriteLine("AudioDeviceService: Registered DllImportResolver for Miscord.Client");

                // Initialize SDL2 audio subsystem
                var result = SDL_Init(SDL_INIT_AUDIO);
                if (result < 0)
                {
                    Console.WriteLine($"AudioDeviceService: SDL_Init(SDL_INIT_AUDIO) failed with code {result}");
                }
                else
                {
                    Console.WriteLine("AudioDeviceService: SDL2 audio subsystem initialized successfully");
                }

                _sdl2Initialized = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AudioDeviceService: Failed to initialize SDL2 - {ex.GetType().Name}: {ex.Message}");
                _sdl2Initialized = true; // Mark as attempted even if failed
            }
        }
    }

    public IReadOnlyList<string> GetOutputDevices()
    {
        try
        {
            EnsureSdl2Initialized();
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
