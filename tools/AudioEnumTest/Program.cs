using System.Reflection;
using System.Runtime.InteropServices;
using SIPSorceryMedia.SDL2;

Console.WriteLine("=== Audio Device Enumeration Test ===\n");
Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine();

// SDL2 P/Invoke
[DllImport("SDL2")]
static extern int SDL_Init(uint flags);

[DllImport("SDL2")]
static extern IntPtr SDL_GetError();

[DllImport("SDL2")]
static extern int SDL_GetNumAudioDevices(int iscapture);

[DllImport("SDL2")]
static extern IntPtr SDL_GetAudioDeviceName(int index, int iscapture);

const uint SDL_INIT_AUDIO = 0x00000010;

// Platform-specific SDL2 paths
string[] GetSdl2Paths()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var paths = new List<string>
        {
            "SDL2.dll",
            Path.Combine(AppContext.BaseDirectory, "SDL2.dll"),
            Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "SDL2.dll"),
        };

        // Also check NuGet packages cache for sdl2.nuget.redist
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var nugetPath = Path.Combine(userProfile, ".nuget", "packages", "sdl2.nuget.redist");
        if (Directory.Exists(nugetPath))
        {
            try
            {
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
            catch { }
        }

        return paths.ToArray();
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        return new[]
        {
            "/opt/homebrew/lib/libSDL2.dylib",
            "/usr/local/lib/libSDL2.dylib",
            "/usr/lib/libSDL2.dylib",
            "libSDL2.dylib",
            "SDL2",
        };
    }
    else // Linux
    {
        return new[]
        {
            "libSDL2-2.0.so.0",
            "libSDL2.so",
            "/usr/lib/x86_64-linux-gnu/libSDL2-2.0.so.0",
            "SDL2",
        };
    }
}

IntPtr ResolveSdl2(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
{
    Console.WriteLine($"[DllResolver] Resolving: {libraryName} for {assembly.GetName().Name}");

    if (libraryName == "SDL2" || libraryName == "libSDL2" || libraryName == "SDL2.dll")
    {
        foreach (var path in GetSdl2Paths())
        {
            Console.WriteLine($"[DllResolver] Trying: {path}");
            if (NativeLibrary.TryLoad(path, out var handle))
            {
                Console.WriteLine($"[DllResolver] SUCCESS: Loaded from {path}");
                return handle;
            }
        }
        Console.WriteLine("[DllResolver] FAILED: Could not load SDL2 from any path");
    }

    return IntPtr.Zero;
}

// Register resolvers
var sdl2HelperAssembly = typeof(SDL2Helper).Assembly;
Console.WriteLine($"SDL2Helper assembly: {sdl2HelperAssembly.GetName().Name}");

try
{
    NativeLibrary.SetDllImportResolver(sdl2HelperAssembly, ResolveSdl2);
    Console.WriteLine("Registered resolver for SDL2Helper assembly");
}
catch (Exception ex)
{
    Console.WriteLine($"Failed to register resolver for SDL2Helper: {ex.Message}");
}

try
{
    NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), ResolveSdl2);
    Console.WriteLine("Registered resolver for current assembly");
}
catch (Exception ex)
{
    Console.WriteLine($"Failed to register resolver for current assembly: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Initializing SDL2 ===\n");

try
{
    var result = SDL_Init(SDL_INIT_AUDIO);
    if (result < 0)
    {
        var error = Marshal.PtrToStringAnsi(SDL_GetError()) ?? "Unknown error";
        Console.WriteLine($"SDL_Init FAILED: {error}");
    }
    else
    {
        Console.WriteLine("SDL_Init SUCCESS");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"SDL_Init EXCEPTION: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}

Console.WriteLine();
Console.WriteLine("=== Direct SDL2 Enumeration ===\n");

try
{
    var numInput = SDL_GetNumAudioDevices(1);
    Console.WriteLine($"Input devices (direct): {numInput}");
    for (int i = 0; i < numInput; i++)
    {
        var namePtr = SDL_GetAudioDeviceName(i, 1);
        var name = Marshal.PtrToStringAnsi(namePtr) ?? "(null)";
        Console.WriteLine($"  [{i}] {name}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Input enumeration FAILED: {ex.Message}");
}

try
{
    var numOutput = SDL_GetNumAudioDevices(0);
    Console.WriteLine($"Output devices (direct): {numOutput}");
    for (int i = 0; i < numOutput; i++)
    {
        var namePtr = SDL_GetAudioDeviceName(i, 0);
        var name = Marshal.PtrToStringAnsi(namePtr) ?? "(null)";
        Console.WriteLine($"  [{i}] {name}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Output enumeration FAILED: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== SDL2Helper Enumeration ===\n");

try
{
    var inputDevices = SDL2Helper.GetAudioRecordingDevices();
    Console.WriteLine($"Input devices (SDL2Helper): {inputDevices.Count}");
    foreach (var device in inputDevices)
    {
        Console.WriteLine($"  - {device}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"SDL2Helper input FAILED: {ex.GetType().Name}: {ex.Message}");
}

try
{
    var outputDevices = SDL2Helper.GetAudioPlaybackDevices();
    Console.WriteLine($"Output devices (SDL2Helper): {outputDevices.Count}");
    foreach (var device in outputDevices)
    {
        Console.WriteLine($"  - {device}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"SDL2Helper output FAILED: {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Test Complete ===");
