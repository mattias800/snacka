using System.Runtime.InteropServices;

namespace Snacka.Client.Services;

/// <summary>
/// Factory for creating platform-specific virtual controller services.
/// </summary>
public static class VirtualControllerServiceFactory
{
    /// <summary>
    /// Creates the appropriate virtual controller service for the current platform.
    /// </summary>
    public static IVirtualControllerService Create()
    {
#if WINDOWS
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowsVirtualControllerService();
        }
#endif

#if LINUX
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new LinuxVirtualControllerService();
        }
#endif

        // Unsupported platform (macOS, etc.)
        return new NullVirtualControllerService();
    }
}
