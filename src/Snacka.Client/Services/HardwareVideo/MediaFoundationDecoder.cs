using System.Runtime.InteropServices;

namespace Snacka.Client.Services.HardwareVideo;

/// <summary>
/// Hardware video decoder using Windows Media Foundation.
/// Decodes H264 on the GPU and renders directly to a D3D11 surface.
/// Zero-copy pipeline: H264 → Media Foundation → D3D11 Texture → Display
///
/// IMPLEMENTATION STATUS: STUB - Requires native library implementation
/// See docs/hardware-video/WINDOWS_IMPLEMENTATION.md for full implementation guide.
/// </summary>
public class MediaFoundationDecoder : IHardwareVideoDecoder
{
    private const string LibraryName = "SnackaWindowsRenderer.dll";

    private nint _handle;
    private int _width;
    private int _height;
    private bool _isInitialized;
    private bool _isDisposed;

    #region P/Invoke

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint mf_decoder_create();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mf_decoder_destroy(nint decoder);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool mf_decoder_initialize(
        nint decoder,
        int width,
        int height,
        nint spsData,
        int spsLength,
        nint ppsData,
        int ppsLength);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool mf_decoder_decode_and_render(
        nint decoder,
        nint nalData,
        int nalLength,
        [MarshalAs(UnmanagedType.I1)] bool isKeyframe);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint mf_decoder_get_view(nint decoder);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mf_decoder_set_display_size(nint decoder, int width, int height);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool mf_decoder_is_available();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mf_decoder_get_output_count(nint decoder);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mf_decoder_get_need_input_count(nint decoder);

    #endregion

    public bool IsInitialized => _isInitialized;
    public (int Width, int Height) VideoDimensions => (_width, _height);
    public nint NativeViewHandle => _handle != nint.Zero ? mf_decoder_get_view(_handle) : nint.Zero;

    private MediaFoundationDecoder(nint handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// Checks if Media Foundation H264 hardware decoding is available.
    /// </summary>
    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            return mf_decoder_is_available();
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
    /// Creates a new Media Foundation decoder instance.
    /// </summary>
    public static MediaFoundationDecoder? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var handle = mf_decoder_create();
            if (handle == nint.Zero)
            {
                return null;
            }
            return new MediaFoundationDecoder(handle);
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
            if (mf_decoder_initialize(_handle, width, height, (nint)spsPtr, sps.Length, (nint)ppsPtr, pps.Length))
            {
                _isInitialized = true;
                return true;
            }
        }

        return false;
    }

    private static int _decodeCallCount = 0;

    public unsafe bool DecodeAndRender(ReadOnlySpan<byte> nalUnit, bool isKeyframe)
    {
        _decodeCallCount++;

        if (!_isInitialized || _isDisposed || _handle == nint.Zero)
        {
            Console.WriteLine($"MediaFoundationDecoder.DecodeAndRender: not ready (init={_isInitialized}, disposed={_isDisposed}, handle={_handle})");
            return false;
        }

        fixed (byte* nalPtr = nalUnit)
        {
            bool result = mf_decoder_decode_and_render(_handle, (nint)nalPtr, nalUnit.Length, isKeyframe);

            // Log stats to see if decoder is producing output
            if (_decodeCallCount <= 5 || _decodeCallCount % 100 == 0)
            {
                int outputCount = mf_decoder_get_output_count(_handle);
                int needInputCount = mf_decoder_get_need_input_count(_handle);
                Console.WriteLine($"MediaFoundationDecoder: call {_decodeCallCount}, output={outputCount}, needInput={needInputCount}");
            }
            return result;
        }
    }

    public void SetDisplaySize(int width, int height)
    {
        if (_handle != nint.Zero)
        {
            mf_decoder_set_display_size(_handle, width, height);
        }
    }

    public void DetachView()
    {
        // Windows native view re-parenting is handled by the system
        // No explicit detach needed
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        if (_handle != nint.Zero)
        {
            mf_decoder_destroy(_handle);
            _handle = nint.Zero;
        }

        _isInitialized = false;
    }
}
