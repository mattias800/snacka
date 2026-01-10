using System.Runtime.InteropServices;

namespace Miscord.Client.Services.HardwareVideo;

/// <summary>
/// Hardware video decoder using Linux VA-API (Video Acceleration API).
/// Decodes H264 on the GPU and renders directly via EGL/OpenGL.
/// Zero-copy pipeline: H264 → VA-API → DMA-BUF → EGL Texture → Display
///
/// IMPLEMENTATION STATUS: STUB - Requires native library implementation
/// See docs/hardware-video/LINUX_IMPLEMENTATION.md for full implementation guide.
/// </summary>
public class VaapiDecoder : IHardwareVideoDecoder
{
    private const string LibraryName = "libMiscordLinuxRenderer.so";

    private nint _handle;
    private int _width;
    private int _height;
    private bool _isInitialized;
    private bool _isDisposed;

    #region P/Invoke

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint va_decoder_create();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void va_decoder_destroy(nint decoder);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool va_decoder_initialize(
        nint decoder,
        int width,
        int height,
        nint spsData,
        int spsLength,
        nint ppsData,
        int ppsLength);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool va_decoder_decode_and_render(
        nint decoder,
        nint nalData,
        int nalLength,
        [MarshalAs(UnmanagedType.I1)] bool isKeyframe);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint va_decoder_get_view(nint decoder);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void va_decoder_set_display_size(nint decoder, int width, int height);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool va_decoder_is_available();

    #endregion

    public bool IsInitialized => _isInitialized;
    public (int Width, int Height) VideoDimensions => (_width, _height);
    public nint NativeViewHandle => _handle != nint.Zero ? va_decoder_get_view(_handle) : nint.Zero;

    private VaapiDecoder(nint handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// Checks if VA-API H264 hardware decoding is available.
    /// </summary>
    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsLinux())
            return false;

        try
        {
            return va_decoder_is_available();
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a new VA-API decoder instance.
    /// </summary>
    public static VaapiDecoder? TryCreate()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        try
        {
            var handle = va_decoder_create();
            if (handle == nint.Zero)
            {
                return null;
            }
            return new VaapiDecoder(handle);
        }
        catch
        {
            return null;
        }
    }

    public unsafe bool Initialize(int width, int height, ReadOnlySpan<byte> sps, ReadOnlySpan<byte> pps)
    {
        if (_isDisposed || _handle == nint.Zero)
            return false;

        _width = width;
        _height = height;

        fixed (byte* spsPtr = sps)
        fixed (byte* ppsPtr = pps)
        {
            if (va_decoder_initialize(_handle, width, height, (nint)spsPtr, sps.Length, (nint)ppsPtr, pps.Length))
            {
                _isInitialized = true;
                return true;
            }
        }

        return false;
    }

    public unsafe bool DecodeAndRender(ReadOnlySpan<byte> nalUnit, bool isKeyframe)
    {
        if (!_isInitialized || _isDisposed || _handle == nint.Zero)
            return false;

        fixed (byte* nalPtr = nalUnit)
        {
            return va_decoder_decode_and_render(_handle, (nint)nalPtr, nalUnit.Length, isKeyframe);
        }
    }

    public void SetDisplaySize(int width, int height)
    {
        if (_handle != nint.Zero)
        {
            va_decoder_set_display_size(_handle, width, height);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        if (_handle != nint.Zero)
        {
            va_decoder_destroy(_handle);
            _handle = nint.Zero;
        }

        _isInitialized = false;
    }
}
