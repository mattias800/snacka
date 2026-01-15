namespace Snacka.Client.Services.WebRtc;

/// <summary>
/// Packet types for the unified stderr protocol from native capture tools.
/// </summary>
public enum StderrPacketType
{
    Unknown,
    Audio,    // MCAP - Audio packet (big-endian magic)
    Preview,  // PREV - Preview frame (big-endian magic)
    Log       // LOGM - Log message (big-endian magic)
}

/// <summary>
/// Preview format values matching native Protocol.h
/// </summary>
public enum PreviewFormat : byte
{
    NV12 = 0,
    RGB24 = 1,
    RGBA32 = 2
}

/// <summary>
/// Parsed audio packet from stderr.
/// </summary>
public struct AudioPacket
{
    public uint SampleCount;
    public uint SampleRate;
    public byte BitsPerSample;
    public byte Channels;
    public bool IsFloat;
    public byte[] PcmData;
}

/// <summary>
/// Parsed preview frame packet from stderr.
/// </summary>
public struct PreviewPacket
{
    public ushort Width;
    public ushort Height;
    public PreviewFormat Format;
    public ulong Timestamp;
    public byte[] PixelData;
}

/// <summary>
/// Parses the unified stderr packet protocol from native capture tools.
/// Handles MCAP (audio), PREV (preview), and LOGM (log) packets.
/// </summary>
public class StderrPacketParser
{
    // Magic numbers - all use big-endian (network byte order)
    private const uint AudioMagic = 0x4D434150;     // "MCAP" big-endian (bytes: 4D 43 41 50)
    private const uint PreviewMagic = 0x50524556;   // "PREV" big-endian
    private const uint LogMagic = 0x4C4F474D;       // "LOGM" big-endian

    private readonly Stream _stream;
    private readonly byte[] _scanBuffer = new byte[4];
    private int _scanIndex;
    private int _skippedBytes;

    public event Action<AudioPacket>? OnAudioPacket;
    public event Action<PreviewPacket>? OnPreviewPacket;
    public event Action<string>? OnLogMessage;

    public int SkippedBytes => _skippedBytes;

    public StderrPacketParser(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>
    /// Reads and parses packets from the stream until cancelled or stream ends.
    /// </summary>
    public void ParseLoop(CancellationToken token)
    {
        var packetCount = 0;

        while (!token.IsCancellationRequested)
        {
            var packetType = ScanForMagic(token);
            if (packetType == StderrPacketType.Unknown)
                break;

            try
            {
                switch (packetType)
                {
                    case StderrPacketType.Audio:
                        var audioPacket = ReadAudioPacket(token);
                        if (audioPacket.HasValue)
                        {
                            OnAudioPacket?.Invoke(audioPacket.Value);
                        }
                        break;

                    case StderrPacketType.Preview:
                        var previewPacket = ReadPreviewPacket(token);
                        if (previewPacket.HasValue)
                        {
                            OnPreviewPacket?.Invoke(previewPacket.Value);
                        }
                        break;

                    case StderrPacketType.Log:
                        var logMessage = ReadLogPacket(token);
                        if (logMessage != null)
                        {
                            OnLogMessage?.Invoke(logMessage);
                        }
                        break;
                }

                packetCount++;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    Console.WriteLine($"StderrPacketParser: Error parsing packet: {ex.Message}");
                }
            }

            _scanIndex = 0;
        }
    }

    private StderrPacketType ScanForMagic(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var b = _stream.ReadByte();
            if (b < 0) return StderrPacketType.Unknown;

            // Shift scan buffer
            _scanBuffer[0] = _scanBuffer[1];
            _scanBuffer[1] = _scanBuffer[2];
            _scanBuffer[2] = _scanBuffer[3];
            _scanBuffer[3] = (byte)b;
            _scanIndex++;

            if (_scanIndex < 4) continue;

            // Check for magic numbers (all big-endian - network byte order)
            var magicBE = (uint)((_scanBuffer[0] << 24) | (_scanBuffer[1] << 16) |
                                 (_scanBuffer[2] << 8) | _scanBuffer[3]);

            if (magicBE == AudioMagic)
            {
                return StderrPacketType.Audio;
            }
            if (magicBE == PreviewMagic)
            {
                return StderrPacketType.Preview;
            }
            if (magicBE == LogMagic)
            {
                return StderrPacketType.Log;
            }

            if (_scanIndex > 4)
            {
                _skippedBytes++;
            }
        }

        return StderrPacketType.Unknown;
    }

    private AudioPacket? ReadAudioPacket(CancellationToken token)
    {
        // Read next 4 bytes to determine version
        var peekBuffer = new byte[4];
        if (!ReadExact(peekBuffer, 0, 4, token)) return null;

        var byte4 = peekBuffer[0]; // version
        var byte5 = peekBuffer[1]; // bitsPerSample
        var byte6 = peekBuffer[2]; // channels
        var byte7 = peekBuffer[3]; // isFloat

        // V2 header: version=2, bitsPerSample=16|32, channels=1-8
        var isV2 = byte4 == 2 && (byte5 == 16 || byte5 == 32) && byte6 >= 1 && byte6 <= 8;

        int headerSize = isV2 ? 24 : 16;
        var headerBuffer = new byte[headerSize];
        Array.Copy(_scanBuffer, 0, headerBuffer, 0, 4); // magic
        Array.Copy(peekBuffer, 0, headerBuffer, 4, 4);  // peek bytes

        // Read rest of header
        if (headerSize > 8)
        {
            if (!ReadExact(headerBuffer, 8, headerSize - 8, token)) return null;
        }

        uint sampleCount;
        uint sampleRate;
        byte bitsPerSample;
        byte channels;
        bool isFloat;

        if (isV2)
        {
            bitsPerSample = headerBuffer[5];
            channels = headerBuffer[6];
            isFloat = headerBuffer[7] != 0;
            sampleCount = BitConverter.ToUInt32(headerBuffer, 8);
            sampleRate = BitConverter.ToUInt32(headerBuffer, 12);
        }
        else
        {
            // V1: 16-bit stereo 48kHz
            sampleCount = BitConverter.ToUInt32(headerBuffer, 4);
            bitsPerSample = 16;
            channels = 2;
            isFloat = false;
            sampleRate = 48000;
        }

        if (sampleCount > 48000 * 10) // Sanity check
        {
            Console.WriteLine($"StderrPacketParser: Invalid audio sample count {sampleCount}");
            return null;
        }

        var bytesPerFrame = (bitsPerSample / 8) * channels;
        var audioSize = (int)(sampleCount * bytesPerFrame);
        var audioBuffer = new byte[audioSize];

        if (!ReadExact(audioBuffer, 0, audioSize, token)) return null;

        return new AudioPacket
        {
            SampleCount = sampleCount,
            SampleRate = sampleRate,
            BitsPerSample = bitsPerSample,
            Channels = channels,
            IsFloat = isFloat,
            PcmData = audioBuffer
        };
    }

    private PreviewPacket? ReadPreviewPacket(CancellationToken token)
    {
        // Read length (4 bytes, big-endian)
        var lengthBuffer = new byte[4];
        if (!ReadExact(lengthBuffer, 0, 4, token)) return null;

        var payloadLength = (uint)((lengthBuffer[0] << 24) | (lengthBuffer[1] << 16) |
                                   (lengthBuffer[2] << 8) | lengthBuffer[3]);

        if (payloadLength < 13 || payloadLength > 50_000_000) // Sanity check
        {
            Console.WriteLine($"StderrPacketParser: Invalid preview payload length {payloadLength}");
            return null;
        }

        // Read width (2 bytes, big-endian)
        var headerBuffer = new byte[13]; // width(2) + height(2) + format(1) + timestamp(8)
        if (!ReadExact(headerBuffer, 0, 13, token)) return null;

        var width = (ushort)((headerBuffer[0] << 8) | headerBuffer[1]);
        var height = (ushort)((headerBuffer[2] << 8) | headerBuffer[3]);
        var format = (PreviewFormat)headerBuffer[4];
        var timestamp = ((ulong)headerBuffer[5] << 56) | ((ulong)headerBuffer[6] << 48) |
                       ((ulong)headerBuffer[7] << 40) | ((ulong)headerBuffer[8] << 32) |
                       ((ulong)headerBuffer[9] << 24) | ((ulong)headerBuffer[10] << 16) |
                       ((ulong)headerBuffer[11] << 8) | headerBuffer[12];

        // Read pixel data
        var pixelDataSize = (int)(payloadLength - 13);
        var pixelData = new byte[pixelDataSize];
        if (!ReadExact(pixelData, 0, pixelDataSize, token)) return null;

        return new PreviewPacket
        {
            Width = width,
            Height = height,
            Format = format,
            Timestamp = timestamp,
            PixelData = pixelData
        };
    }

    private string? ReadLogPacket(CancellationToken token)
    {
        // Read length (4 bytes, big-endian)
        var lengthBuffer = new byte[4];
        if (!ReadExact(lengthBuffer, 0, 4, token)) return null;

        var payloadLength = (uint)((lengthBuffer[0] << 24) | (lengthBuffer[1] << 16) |
                                   (lengthBuffer[2] << 8) | lengthBuffer[3]);

        if (payloadLength < 1 || payloadLength > 100_000) // Sanity check
        {
            Console.WriteLine($"StderrPacketParser: Invalid log payload length {payloadLength}");
            return null;
        }

        // Read level (1 byte) and message
        var logBuffer = new byte[payloadLength];
        if (!ReadExact(logBuffer, 0, (int)payloadLength, token)) return null;

        var level = logBuffer[0];
        var message = System.Text.Encoding.UTF8.GetString(logBuffer, 1, (int)payloadLength - 1);

        return message;
    }

    private bool ReadExact(byte[] buffer, int offset, int count, CancellationToken token)
    {
        var read = 0;
        while (read < count && !token.IsCancellationRequested)
        {
            var r = _stream.Read(buffer, offset + read, count - read);
            if (r == 0) return false;
            read += r;
        }
        return read == count;
    }
}
