using System.Reflection;
using System.Runtime.InteropServices;
using SIPSorcery.Media;
using SIPSorceryMedia.SDL2;
using SIPSorceryMedia.Abstractions;

namespace Snacka.Client.Services;

public interface IAudioDeviceService : IDisposable
{
    IReadOnlyList<string> GetInputDevices();
    IReadOnlyList<string> GetOutputDevices();

    bool IsTestingInput { get; }
    bool IsLoopbackEnabled { get; }
    float CurrentAgcGain { get; }

    Task StartInputTestAsync(string? deviceName, Action<float> onRmsUpdate, Action<float>? onAgcUpdate = null);
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

    [DllImport("SDL2")]
    private static extern IntPtr SDL_GetError();

    [DllImport("SDL2")]
    private static extern void SDL_ClearError();

    private const uint SDL_INIT_AUDIO = 0x00000010;

    private static string GetSdlError()
    {
        var ptr = SDL_GetError();
        return ptr != IntPtr.Zero ? Marshal.PtrToStringAnsi(ptr) ?? "" : "";
    }

    private readonly ISettingsStore? _settingsStore;
    private SDL2AudioSource? _testAudioSource;
    private SDL2AudioEndPoint? _testAudioSink;
    private AudioFormat? _selectedFormat; // Track the selected format for source/sink consistency
    private Action<float>? _onRmsUpdate;
    private bool _isLoopbackEnabled;
    private CancellationTokenSource? _testCts;
    private readonly AudioResampler _loopbackResampler = new(); // Resampler for loopback audio
    private bool _loggedSampleRate; // Only log sample rate once per test session

    // AGC for microphone test (mirrors WebRtcService AGC)
    private float _testAgcGain = 1.0f;
    private Action<float>? _onAgcUpdate;
    private const float AgcTargetRms = 3000f;         // Target RMS level (reduced from 6000)
    private const float AgcMinGain = 1.0f;
    private const float AgcMaxGain = 8.0f;            // Max 8x boost (reduced from 16x)
    private const float AgcAttackCoeff = 0.1f;
    private const float AgcReleaseCoeff = 0.005f;
    private const float AgcSilenceThreshold = 200f;
    private const float BaselineInputBoost = 1.5f;    // 1.5x baseline (reduced from 4x)

    public bool IsTestingInput => _testAudioSource != null;
    public float CurrentAgcGain => _testAgcGain;
    public bool IsLoopbackEnabled => _isLoopbackEnabled;

    public AudioDeviceService(ISettingsStore? settingsStore = null)
    {
        _settingsStore = settingsStore;
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

    // Get platform-specific SDL2 library paths
    private static string[] GetSdl2Paths()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var paths = new List<string>
            {
                "SDL2.dll",                                                    // Current directory / PATH
                Path.Combine(AppContext.BaseDirectory, "SDL2.dll"),            // App directory
                Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "SDL2.dll"),
            };

            // Also check NuGet packages cache for sdl2.nuget.redist
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var nugetPath = Path.Combine(userProfile, ".nuget", "packages", "sdl2.nuget.redist");
            if (Directory.Exists(nugetPath))
            {
                try
                {
                    // Find the latest version
                    var versionDirs = Directory.GetDirectories(nugetPath);
                    foreach (var versionDir in versionDirs.OrderByDescending(d => d))
                    {
                        var x64Path = Path.Combine(versionDir, "build", "native", "bin", "x64", "SDL2.dll");
                        if (File.Exists(x64Path))
                        {
                            paths.Add(x64Path);
                            break;
                        }
                    }
                }
                catch { /* Ignore errors scanning NuGet cache */ }
            }

            return paths.ToArray();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new[]
            {
                "/opt/homebrew/lib/libSDL2.dylib",      // Apple Silicon Homebrew
                "/usr/local/lib/libSDL2.dylib",         // Intel Homebrew
                "/usr/lib/libSDL2.dylib",               // System
                "libSDL2.dylib",                        // Current directory / PATH
                "SDL2",                                 // Let system find it
            };
        }
        else // Linux
        {
            return new[]
            {
                "libSDL2-2.0.so.0",                              // Common versioned name
                "libSDL2.so",                                    // Unversioned
                "/usr/lib/x86_64-linux-gnu/libSDL2-2.0.so.0",    // Debian/Ubuntu x64
                "/usr/lib64/libSDL2-2.0.so.0",                   // Fedora/RHEL x64
                "/usr/lib/libSDL2-2.0.so.0",                     // Generic
                "SDL2",                                          // Let system find it
            };
        }
    }

    private static IntPtr ResolveSdl2(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == "SDL2" || libraryName == "libSDL2" || libraryName == "SDL2.dll")
        {
            foreach (var path in GetSdl2Paths())
            {
                if (NativeLibrary.TryLoad(path, out var handle))
                {
                    Console.WriteLine($"AudioDeviceService: Loaded SDL2 from: {path}");
                    return handle;
                }
            }
            Console.WriteLine("AudioDeviceService: Failed to load SDL2 from any known path");
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
                Console.WriteLine("AudioDeviceService: Registered DllImportResolver for Snacka.Client");

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

    public async Task StartInputTestAsync(string? deviceName, Action<float> onRmsUpdate, Action<float>? onAgcUpdate = null)
    {
        // Stop any existing test
        await StopTestAsync();

        // Reset AGC for fresh test
        _testAgcGain = 1.0f;

        _onRmsUpdate = onRmsUpdate;
        _onAgcUpdate = onAgcUpdate;
        _testCts = new CancellationTokenSource();

        try
        {
            EnsureSdl2Initialized();

            var audioEncoder = new AudioEncoder(includeOpus: true);
            _testAudioSource = new SDL2AudioSource(deviceName ?? string.Empty, audioEncoder);

            // Subscribe to raw audio samples for RMS calculation
            _testAudioSource.OnAudioSourceRawSample += OnTestAudioSample;

            // Subscribe to error events
            _testAudioSource.OnAudioSourceError += (error) =>
            {
                Console.WriteLine($"AudioDeviceService: Audio source error: {error}");
            };

            // Set audio format before starting - prefer Opus for high quality (48kHz)
            var formats = _testAudioSource.GetAudioSourceFormats();
            if (formats.Count > 0)
            {
                var selectedFormat = formats.FirstOrDefault(f => f.FormatName == "OPUS");
                if (selectedFormat.FormatName == null)
                    selectedFormat = formats.FirstOrDefault(f => f.FormatName == "PCMU");
                if (selectedFormat.FormatName == null)
                    selectedFormat = formats[0];
                _testAudioSource.SetAudioSourceFormat(selectedFormat);
                _selectedFormat = selectedFormat; // Store for loopback sink to use same format
                Console.WriteLine($"AudioDeviceService: Selected format: {selectedFormat.FormatName} ({selectedFormat.ClockRate}Hz)");
            }

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
                    var audioEncoder = new AudioEncoder(includeOpus: true);
                    _testAudioSink = new SDL2AudioEndPoint(outputDevice ?? string.Empty, audioEncoder);

                    // Use the same format as the source to ensure sample rates match
                    // CRITICAL: Must set format BEFORE starting sink to prevent pitch issues
                    if (_selectedFormat.HasValue)
                    {
                        _testAudioSink.SetAudioSinkFormat(_selectedFormat.Value);
                        Console.WriteLine($"AudioDeviceService: Loopback sink using format: {_selectedFormat.Value.FormatName} ({_selectedFormat.Value.ClockRate}Hz)");
                    }
                    else
                    {
                        // Fallback: get OPUS format from encoder if _selectedFormat not set
                        // This prevents using default 8kHz format which causes pitch-down issues
                        var formats = audioEncoder.SupportedFormats;
                        var opusFormat = formats.FirstOrDefault(f => f.FormatName == "OPUS");
                        if (!string.IsNullOrEmpty(opusFormat.FormatName))
                        {
                            _testAudioSink.SetAudioSinkFormat(opusFormat);
                            Console.WriteLine($"AudioDeviceService: Loopback sink fallback format: {opusFormat.FormatName} ({opusFormat.ClockRate}Hz)");
                        }
                        else
                        {
                            Console.WriteLine("AudioDeviceService: WARNING - Could not set loopback sink format, may cause pitch issues!");
                        }
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
        _onAgcUpdate = null;
        _isLoopbackEnabled = false;
        _selectedFormat = null;
        _loopbackResampler.Reset();
        _loggedSampleRate = false;
        Console.WriteLine("AudioDeviceService: Stopped input test");
    }

    private void OnTestAudioSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
    {
        if (sample.Length == 0) return;

        // Get user settings
        var manualGain = _settingsStore?.Settings.InputGain ?? 1.0f;
        var gateEnabled = _settingsStore?.Settings.GateEnabled ?? true;
        var gateThreshold = _settingsStore?.Settings.GateThreshold ?? 0.02f;

        // Step 1: Calculate input RMS before any processing
        double sumOfSquares = 0;
        for (int i = 0; i < sample.Length; i++)
        {
            sumOfSquares += (double)sample[i] * sample[i];
        }
        float inputRms = (float)Math.Sqrt(sumOfSquares / sample.Length);

        // Step 2: Update AGC gain (only if not silence)
        if (inputRms > AgcSilenceThreshold)
        {
            float desiredGain = AgcTargetRms / inputRms;
            desiredGain = Math.Clamp(desiredGain, AgcMinGain, AgcMaxGain);

            if (desiredGain < _testAgcGain)
                _testAgcGain += (desiredGain - _testAgcGain) * AgcAttackCoeff;
            else
                _testAgcGain += (desiredGain - _testAgcGain) * AgcReleaseCoeff;

            // Notify listener of AGC change
            _onAgcUpdate?.Invoke(_testAgcGain);
        }

        // Step 3: Calculate total gain = baseline boost * AGC * manual adjustment
        float totalGain = BaselineInputBoost * _testAgcGain * manualGain;

        // Step 4: Apply total gain to samples
        for (int i = 0; i < sample.Length; i++)
        {
            float gainedSample = sample[i] * totalGain;
            // Soft clipping to prevent harsh distortion
            if (gainedSample > 30000f)
                gainedSample = 30000f + (gainedSample - 30000f) * 0.1f;
            else if (gainedSample < -30000f)
                gainedSample = -30000f + (gainedSample + 30000f) * 0.1f;
            sample[i] = (short)Math.Clamp(gainedSample, short.MinValue, short.MaxValue);
        }

        // Step 5: Calculate output RMS for level display
        sumOfSquares = 0;
        for (int i = 0; i < sample.Length; i++)
        {
            sumOfSquares += (double)sample[i] * sample[i];
        }
        double outputRms = Math.Sqrt(sumOfSquares / sample.Length);

        // Normalize to 0-1 range
        float normalizedRms = (float)Math.Min(1.0, outputRms / 10000.0);

        // Check if above gate threshold
        var isAboveGate = normalizedRms > gateThreshold;

        // If gate is enabled and audio is below threshold, zero out the samples
        if (gateEnabled && !isAboveGate)
        {
            Array.Clear(sample, 0, sample.Length);
            normalizedRms = 0; // Show zero level when gated
        }

        _onRmsUpdate?.Invoke(normalizedRms);

        // If loopback is enabled, send audio to output
        if (_isLoopbackEnabled && _testAudioSink != null)
        {
            // Get the input sample rate and resample to 48kHz if needed
            int inputRateHz = AudioResampler.ToHz(samplingRate);

            // Log sample rate info once per session
            if (!_loggedSampleRate)
            {
                _loggedSampleRate = true;
                Console.WriteLine($"AudioDeviceService: Input sample rate: {inputRateHz}Hz, target: {AudioResampler.TargetSampleRate}Hz");
            }

            // Resample to 48kHz for the sink (which uses OPUS format)
            var outputSamples = inputRateHz == AudioResampler.TargetSampleRate
                ? sample
                : _loopbackResampler.ResampleToCodecRate(sample, inputRateHz);

            byte[] pcmBytes = new byte[outputSamples.Length * 2];
            Buffer.BlockCopy(outputSamples, 0, pcmBytes, 0, pcmBytes.Length);
            _testAudioSink.GotAudioSample(pcmBytes);
        }
    }

    public void Dispose()
    {
        StopTestAsync().GetAwaiter().GetResult();
    }
}
