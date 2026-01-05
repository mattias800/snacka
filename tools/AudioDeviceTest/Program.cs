using System.Reflection;
using System.Runtime.InteropServices;
using SIPSorceryMedia.SDL2;

// Import SDL2 functions directly for initialization
[DllImport("SDL2")]
static extern int SDL_Init(uint flags);

[DllImport("SDL2")]
static extern int SDL_GetNumAudioDevices(int iscapture);

[DllImport("SDL2")]
static extern IntPtr SDL_GetAudioDeviceName(int index, int iscapture);

[DllImport("SDL2")]
static extern IntPtr SDL_GetError();

const uint SDL_INIT_AUDIO = 0x00000010;

Console.WriteLine("=== SDL2 Audio Device Test ===\n");

// Try to set up DllImportResolver for SDL2
var sdl2Assembly = typeof(SDL2Helper).Assembly;
Console.WriteLine($"SDL2Helper assembly: {sdl2Assembly.FullName}");

// Find the SDL2-CS assembly (contains the P/Invoke definitions)
Assembly? sdl2CsAssembly = null;
foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
{
    if (asm.GetName().Name == "SDL2-CS")
    {
        sdl2CsAssembly = asm;
        break;
    }
}

// If not loaded yet, try to load it
if (sdl2CsAssembly == null)
{
    try
    {
        // Trigger loading by referencing SDL2Helper
        var _ = SDL2Helper.GetAudioRecordingDevices;
    }
    catch { }

    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
    {
        if (asm.GetName().Name == "SDL2-CS")
        {
            sdl2CsAssembly = asm;
            break;
        }
    }
}

Console.WriteLine($"SDL2-CS assembly: {sdl2CsAssembly?.FullName ?? "NOT FOUND"}");

// Set up DllImportResolver
var sdl2Paths = new[]
{
    "/opt/homebrew/lib/libSDL2.dylib",      // Apple Silicon Homebrew
    "/usr/local/lib/libSDL2.dylib",         // Intel Homebrew
    "/usr/lib/libSDL2.dylib",               // System
};

IntPtr ResolveSdl2(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
{
    Console.WriteLine($"  DllImportResolver called for: {libraryName} (assembly: {assembly.GetName().Name})");

    if (libraryName == "SDL2" || libraryName == "libSDL2" || libraryName == "SDL2.dll")
    {
        foreach (var path in sdl2Paths)
        {
            if (NativeLibrary.TryLoad(path, out var handle))
            {
                Console.WriteLine($"  -> Loaded from: {path}");
                return handle;
            }
        }
        Console.WriteLine($"  -> Failed to load from any known path");
    }

    return IntPtr.Zero;
}

// Register resolver for both assemblies
if (sdl2CsAssembly != null)
{
    NativeLibrary.SetDllImportResolver(sdl2CsAssembly, ResolveSdl2);
    Console.WriteLine("Registered DllImportResolver for SDL2-CS assembly");
}

NativeLibrary.SetDllImportResolver(sdl2Assembly, ResolveSdl2);
Console.WriteLine("Registered DllImportResolver for SIPSorceryMedia.SDL2 assembly");

// Also register for this assembly (for the local DllImport declarations)
NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), ResolveSdl2);
Console.WriteLine("Registered DllImportResolver for current assembly");

Console.WriteLine();

// Now try to enumerate devices
Console.WriteLine("=== Attempting to enumerate audio devices ===\n");

// First, initialize SDL2 audio subsystem
Console.WriteLine("Initializing SDL2 audio subsystem...");
try
{
    var initResult = SDL_Init(SDL_INIT_AUDIO);
    if (initResult < 0)
    {
        var error = Marshal.PtrToStringAnsi(SDL_GetError());
        Console.WriteLine($"SDL_Init failed with error: {error}");
    }
    else
    {
        Console.WriteLine("SDL_Init succeeded!");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"SDL_Init threw exception: {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine();

// Try direct SDL2 calls
Console.WriteLine("=== Direct SDL2 API calls ===\n");

try
{
    var numInputDevices = SDL_GetNumAudioDevices(1); // 1 = capture
    Console.WriteLine($"SDL_GetNumAudioDevices(capture): {numInputDevices}");
    for (int i = 0; i < numInputDevices; i++)
    {
        var namePtr = SDL_GetAudioDeviceName(i, 1);
        var name = Marshal.PtrToStringAnsi(namePtr);
        Console.WriteLine($"  Input {i}: {name}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Direct input enumeration error: {ex.GetType().Name}: {ex.Message}");
}

try
{
    var numOutputDevices = SDL_GetNumAudioDevices(0); // 0 = playback
    Console.WriteLine($"SDL_GetNumAudioDevices(playback): {numOutputDevices}");
    for (int i = 0; i < numOutputDevices; i++)
    {
        var namePtr = SDL_GetAudioDeviceName(i, 0);
        var name = Marshal.PtrToStringAnsi(namePtr);
        Console.WriteLine($"  Output {i}: {name}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Direct output enumeration error: {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== SDL2Helper API calls ===\n");

try
{
    Console.WriteLine("Getting recording devices via SDL2Helper...");
    var inputDevices = SDL2Helper.GetAudioRecordingDevices();
    Console.WriteLine($"Found {inputDevices.Count} input devices:");
    foreach (var device in inputDevices)
    {
        Console.WriteLine($"  - {device}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR getting input devices: {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine();

try
{
    Console.WriteLine("Getting playback devices via SDL2Helper...");
    var outputDevices = SDL2Helper.GetAudioPlaybackDevices();
    Console.WriteLine($"Found {outputDevices.Count} output devices:");
    foreach (var device in outputDevices)
    {
        Console.WriteLine($"  - {device}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR getting output devices: {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine("\n=== Test complete ===");
