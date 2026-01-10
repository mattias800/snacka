using System.Runtime.InteropServices;

namespace Miscord.Client.Services.HardwareVideo;

/// <summary>
/// Hardware video decoder using Apple VideoToolbox.
/// Decodes H264 on the GPU and renders directly to a Metal surface.
/// Zero-copy pipeline: H264 → VideoToolbox → CVPixelBuffer → Metal texture → Display
/// </summary>
public class VideoToolboxDecoder : IHardwareVideoDecoder
{
    private const string LibraryName = "libMiscordMetalRenderer.dylib";

    private nint _handle;
    private int _width;
    private int _height;
    private bool _isInitialized;
    private bool _isDisposed;

    #region P/Invoke

    [DllImport(LibraryName)]
    private static extern nint vt_decoder_create();

    [DllImport(LibraryName)]
    private static extern void vt_decoder_destroy(nint decoder);

    [DllImport(LibraryName)]
    private static extern bool vt_decoder_initialize(
        nint decoder,
        int width,
        int height,
        nint spsData,
        int spsLength,
        nint ppsData,
        int ppsLength);

    [DllImport(LibraryName)]
    private static extern bool vt_decoder_decode_and_render(
        nint decoder,
        nint nalData,
        int nalLength,
        bool isKeyframe);

    [DllImport(LibraryName)]
    private static extern nint vt_decoder_get_view(nint decoder);

    [DllImport(LibraryName)]
    private static extern void vt_decoder_set_display_size(nint decoder, int width, int height);

    [DllImport(LibraryName)]
    private static extern bool vt_decoder_is_available();

    #endregion

    public bool IsInitialized => _isInitialized;
    public (int Width, int Height) VideoDimensions => (_width, _height);
    public nint NativeViewHandle => _handle != nint.Zero ? vt_decoder_get_view(_handle) : nint.Zero;

    private VideoToolboxDecoder(nint handle)
    {
        _handle = handle;
    }

    public static bool IsAvailable()
    {
        try
        {
            return vt_decoder_is_available();
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

    public static VideoToolboxDecoder? TryCreate()
    {
        try
        {
            var handle = vt_decoder_create();
            if (handle == nint.Zero)
            {
                return null;
            }
            return new VideoToolboxDecoder(handle);
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
            if (vt_decoder_initialize(_handle, width, height, (nint)spsPtr, sps.Length, (nint)ppsPtr, pps.Length))
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
            return vt_decoder_decode_and_render(_handle, (nint)nalPtr, nalUnit.Length, isKeyframe);
        }
    }

    public void SetDisplaySize(int width, int height)
    {
        if (_handle != nint.Zero)
        {
            vt_decoder_set_display_size(_handle, width, height);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        if (_handle != nint.Zero)
        {
            vt_decoder_destroy(_handle);
            _handle = nint.Zero;
        }

        _isInitialized = false;
    }
}
