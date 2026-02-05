namespace Snacka.Client.Services.Audio;

/// <summary>
/// MCAP (Microphone Capture Audio Protocol) header structure.
/// 24-byte binary header with the following format:
/// - Bytes 0-3:   Magic (0x4D434150 = "MCAP" big-endian)
/// - Byte 4:      Version
/// - Byte 5:      BitsPerSample
/// - Byte 6:      Channels
/// - Byte 7:      IsFloat (0 = int, 1 = float)
/// - Bytes 8-11:  SampleCount (little-endian)
/// - Bytes 12-15: SampleRate (little-endian)
/// - Bytes 16-23: Timestamp in milliseconds (little-endian)
/// </summary>
public class McapHeader
{
    public byte Version { get; set; }
    public byte BitsPerSample { get; set; }
    public byte Channels { get; set; }
    public byte IsFloat { get; set; }
    public uint SampleCount { get; set; }
    public uint SampleRate { get; set; }
    public ulong Timestamp { get; set; }

    /// <summary>
    /// Calculates the audio data size in bytes based on the header.
    /// </summary>
    public int AudioDataSize => (int)SampleCount * Channels * (BitsPerSample / 8);
}

/// <summary>
/// Parser for MCAP (Microphone Capture Audio Protocol) binary format.
/// Used to read audio data from native capture processes.
/// This class is stateless and thread-safe.
/// </summary>
public static class McapParser
{
    /// <summary>
    /// MCAP header size in bytes.
    /// </summary>
    public const int HeaderSize = 24;

    /// <summary>
    /// MCAP magic number (0x4D434150 = "MCAP" in big-endian).
    /// </summary>
    public const uint Magic = 0x4D434150;

    /// <summary>
    /// Magic bytes for scanning: 'M', 'C', 'A', 'P'.
    /// </summary>
    public static readonly byte[] MagicBytes = { 0x4D, 0x43, 0x41, 0x50 };

    /// <summary>
    /// Parses an MCAP header from a 24-byte buffer.
    /// </summary>
    /// <param name="buffer">Buffer containing header data (must be at least 24 bytes)</param>
    /// <returns>Parsed header, or null if magic bytes don't match</returns>
    /// <exception cref="ArgumentException">Thrown if buffer is less than 24 bytes</exception>
    public static McapHeader? ParseHeader(byte[] buffer)
    {
        if (buffer.Length < HeaderSize)
        {
            throw new ArgumentException($"Buffer must be at least {HeaderSize} bytes", nameof(buffer));
        }

        return ParseHeader(buffer.AsSpan());
    }

    /// <summary>
    /// Parses an MCAP header from a span of bytes.
    /// </summary>
    /// <param name="buffer">Span containing header data (must be at least 24 bytes)</param>
    /// <returns>Parsed header, or null if magic bytes don't match</returns>
    public static McapHeader? ParseHeader(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderSize)
        {
            throw new ArgumentException($"Buffer must be at least {HeaderSize} bytes", nameof(buffer));
        }

        // Read magic (big-endian - this is the only big-endian field)
        uint magic = (uint)(buffer[0] << 24 | buffer[1] << 16 | buffer[2] << 8 | buffer[3]);
        if (magic != Magic)
        {
            return null;
        }

        // All other multi-byte fields are little-endian (native byte order from Swift/C++)
        return new McapHeader
        {
            Version = buffer[4],
            BitsPerSample = buffer[5],
            Channels = buffer[6],
            IsFloat = buffer[7],
            // Little-endian: least significant byte first
            SampleCount = (uint)(buffer[8] | buffer[9] << 8 | buffer[10] << 16 | buffer[11] << 24),
            SampleRate = (uint)(buffer[12] | buffer[13] << 8 | buffer[14] << 16 | buffer[15] << 24),
            Timestamp = (ulong)(
                (ulong)buffer[16] | (ulong)buffer[17] << 8 |
                (ulong)buffer[18] << 16 | (ulong)buffer[19] << 24 |
                (ulong)buffer[20] << 32 | (ulong)buffer[21] << 40 |
                (ulong)buffer[22] << 48 | (ulong)buffer[23] << 56)
        };
    }

    /// <summary>
    /// Scans a buffer for the MCAP magic bytes starting at the given offset.
    /// </summary>
    /// <param name="buffer">Buffer to scan</param>
    /// <param name="startOffset">Offset to start scanning from</param>
    /// <returns>Offset where magic was found, or -1 if not found</returns>
    public static int ScanForMagic(byte[] buffer, int startOffset = 0)
    {
        return ScanForMagic(buffer.AsSpan(), startOffset);
    }

    /// <summary>
    /// Scans a span for the MCAP magic bytes starting at the given offset.
    /// </summary>
    /// <param name="buffer">Span to scan</param>
    /// <param name="startOffset">Offset to start scanning from</param>
    /// <returns>Offset where magic was found, or -1 if not found</returns>
    public static int ScanForMagic(ReadOnlySpan<byte> buffer, int startOffset = 0)
    {
        // Need at least 4 bytes for magic
        if (buffer.Length < 4 || startOffset > buffer.Length - 4)
        {
            return -1;
        }

        for (int i = startOffset; i <= buffer.Length - 4; i++)
        {
            if (buffer[i] == MagicBytes[0] &&
                buffer[i + 1] == MagicBytes[1] &&
                buffer[i + 2] == MagicBytes[2] &&
                buffer[i + 3] == MagicBytes[3])
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Validates an MCAP header for reasonable values.
    /// </summary>
    /// <param name="header">Header to validate</param>
    /// <returns>True if header values are reasonable, false otherwise</returns>
    public static bool ValidateHeader(McapHeader header)
    {
        // Validate bits per sample (8, 16, 24, 32 are common)
        if (header.BitsPerSample != 8 && header.BitsPerSample != 16 &&
            header.BitsPerSample != 24 && header.BitsPerSample != 32)
        {
            return false;
        }

        // Validate channels (1-8 reasonable range)
        if (header.Channels < 1 || header.Channels > 8)
        {
            return false;
        }

        // Validate sample rate (common rates: 8000, 16000, 22050, 44100, 48000, 96000)
        if (header.SampleRate < 8000 || header.SampleRate > 192000)
        {
            return false;
        }

        // Validate sample count (reasonable limit to prevent memory issues)
        // 48000 samples/sec * 10 seconds = 480000 max reasonable
        if (header.SampleCount > 480000)
        {
            return false;
        }

        // IsFloat should be 0 or 1
        if (header.IsFloat > 1)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Creates an MCAP header byte array from header values.
    /// Useful for testing and creating mock MCAP data.
    /// </summary>
    public static byte[] CreateHeader(
        uint sampleCount,
        uint sampleRate = 48000,
        byte bitsPerSample = 16,
        byte channels = 2,
        byte isFloat = 0,
        byte version = 1,
        ulong timestamp = 0)
    {
        var buffer = new byte[HeaderSize];

        // Magic (big-endian)
        buffer[0] = MagicBytes[0];
        buffer[1] = MagicBytes[1];
        buffer[2] = MagicBytes[2];
        buffer[3] = MagicBytes[3];

        // Metadata
        buffer[4] = version;
        buffer[5] = bitsPerSample;
        buffer[6] = channels;
        buffer[7] = isFloat;

        // SampleCount (little-endian)
        buffer[8] = (byte)(sampleCount & 0xFF);
        buffer[9] = (byte)((sampleCount >> 8) & 0xFF);
        buffer[10] = (byte)((sampleCount >> 16) & 0xFF);
        buffer[11] = (byte)((sampleCount >> 24) & 0xFF);

        // SampleRate (little-endian)
        buffer[12] = (byte)(sampleRate & 0xFF);
        buffer[13] = (byte)((sampleRate >> 8) & 0xFF);
        buffer[14] = (byte)((sampleRate >> 16) & 0xFF);
        buffer[15] = (byte)((sampleRate >> 24) & 0xFF);

        // Timestamp (little-endian)
        buffer[16] = (byte)(timestamp & 0xFF);
        buffer[17] = (byte)((timestamp >> 8) & 0xFF);
        buffer[18] = (byte)((timestamp >> 16) & 0xFF);
        buffer[19] = (byte)((timestamp >> 24) & 0xFF);
        buffer[20] = (byte)((timestamp >> 32) & 0xFF);
        buffer[21] = (byte)((timestamp >> 40) & 0xFF);
        buffer[22] = (byte)((timestamp >> 48) & 0xFF);
        buffer[23] = (byte)((timestamp >> 56) & 0xFF);

        return buffer;
    }

    /// <summary>
    /// Scans a stream byte-by-byte until MCAP magic is found.
    /// Fills the header buffer with the complete header once magic is found.
    /// </summary>
    /// <param name="stream">Stream to read from</param>
    /// <param name="headerBuffer">Buffer to fill with header (must be at least 24 bytes)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if header was found and buffer filled, false on EOF</returns>
    public static async Task<bool> ScanForMagicAsync(
        Stream stream,
        byte[] headerBuffer,
        CancellationToken cancellationToken = default)
    {
        if (headerBuffer.Length < HeaderSize)
        {
            throw new ArgumentException($"Header buffer must be at least {HeaderSize} bytes", nameof(headerBuffer));
        }

        var singleByte = new byte[1];
        int matchIndex = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            int bytesRead = await stream.ReadAsync(singleByte.AsMemory(0, 1), cancellationToken);
            if (bytesRead == 0) return false; // EOF

            if (singleByte[0] == MagicBytes[matchIndex])
            {
                headerBuffer[matchIndex] = singleByte[0];
                matchIndex++;

                if (matchIndex == 4)
                {
                    // Found magic! Now read the rest of the header
                    int read = 4;
                    while (read < HeaderSize && !cancellationToken.IsCancellationRequested)
                    {
                        bytesRead = await stream.ReadAsync(
                            headerBuffer.AsMemory(read, HeaderSize - read), cancellationToken);
                        if (bytesRead == 0) return false;
                        read += bytesRead;
                    }
                    return true;
                }
            }
            else
            {
                // Reset match
                matchIndex = 0;
                // Check if current byte starts a new match
                if (singleByte[0] == MagicBytes[0])
                {
                    headerBuffer[0] = singleByte[0];
                    matchIndex = 1;
                }
            }
        }

        return false;
    }
}
