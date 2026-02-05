using Snacka.Client.Services.Audio;

namespace Snacka.Client.Tests;

/// <summary>
/// Tests for the McapParser class to verify MCAP protocol parsing behavior.
/// These tests validate header parsing, magic byte detection, and edge cases
/// to prevent regressions in the native audio capture protocol.
/// </summary>
public class McapParserTests
{
    #region Constants Tests

    [Fact]
    public void HeaderSize_Is24Bytes()
    {
        Assert.Equal(24, McapParser.HeaderSize);
    }

    [Fact]
    public void Magic_IsMCAP()
    {
        // 0x4D434150 = 'M' 'C' 'A' 'P' in big-endian
        Assert.Equal(0x4D434150u, McapParser.Magic);
    }

    [Fact]
    public void MagicBytes_AreMCAP()
    {
        Assert.Equal(4, McapParser.MagicBytes.Length);
        Assert.Equal((byte)'M', McapParser.MagicBytes[0]);
        Assert.Equal((byte)'C', McapParser.MagicBytes[1]);
        Assert.Equal((byte)'A', McapParser.MagicBytes[2]);
        Assert.Equal((byte)'P', McapParser.MagicBytes[3]);
    }

    #endregion

    #region ParseHeader Tests

    [Fact]
    public void ParseHeader_ValidHeader_ReturnsHeader()
    {
        var buffer = McapParser.CreateHeader(
            sampleCount: 480,
            sampleRate: 48000,
            bitsPerSample: 16,
            channels: 2,
            isFloat: 0,
            version: 1,
            timestamp: 123456789);

        var header = McapParser.ParseHeader(buffer);

        Assert.NotNull(header);
        Assert.Equal(1, header.Version);
        Assert.Equal(16, header.BitsPerSample);
        Assert.Equal(2, header.Channels);
        Assert.Equal(0, header.IsFloat);
        Assert.Equal(480u, header.SampleCount);
        Assert.Equal(48000u, header.SampleRate);
        Assert.Equal(123456789ul, header.Timestamp);
    }

    [Fact]
    public void ParseHeader_InvalidMagic_ReturnsNull()
    {
        var buffer = new byte[24];
        buffer[0] = (byte)'X'; // Wrong magic
        buffer[1] = (byte)'C';
        buffer[2] = (byte)'A';
        buffer[3] = (byte)'P';

        var header = McapParser.ParseHeader(buffer);

        Assert.Null(header);
    }

    [Fact]
    public void ParseHeader_AllZeros_ReturnsNull()
    {
        var buffer = new byte[24]; // All zeros

        var header = McapParser.ParseHeader(buffer);

        Assert.Null(header);
    }

    [Fact]
    public void ParseHeader_BufferTooSmall_ThrowsArgumentException()
    {
        var buffer = new byte[23]; // One byte too small

        Assert.Throws<ArgumentException>(() => McapParser.ParseHeader(buffer));
    }

    [Fact]
    public void ParseHeader_BufferLargerThan24_Succeeds()
    {
        var buffer = new byte[100];
        var header = McapParser.CreateHeader(sampleCount: 100);
        Array.Copy(header, buffer, 24);

        var result = McapParser.ParseHeader(buffer);

        Assert.NotNull(result);
        Assert.Equal(100u, result.SampleCount);
    }

    [Fact]
    public void ParseHeader_MagicIsBigEndian()
    {
        // Verify magic is read as big-endian (most significant byte first)
        var buffer = new byte[24];
        buffer[0] = 0x4D; // 'M' - most significant
        buffer[1] = 0x43; // 'C'
        buffer[2] = 0x41; // 'A'
        buffer[3] = 0x50; // 'P' - least significant

        var header = McapParser.ParseHeader(buffer);

        Assert.NotNull(header);
    }

    [Fact]
    public void ParseHeader_SampleCountIsLittleEndian()
    {
        var buffer = McapParser.CreateHeader(sampleCount: 0x12345678);

        var header = McapParser.ParseHeader(buffer);

        Assert.NotNull(header);
        Assert.Equal(0x12345678u, header.SampleCount);

        // Verify little-endian byte order in buffer
        Assert.Equal(0x78, buffer[8]);  // Least significant
        Assert.Equal(0x56, buffer[9]);
        Assert.Equal(0x34, buffer[10]);
        Assert.Equal(0x12, buffer[11]); // Most significant
    }

    [Fact]
    public void ParseHeader_SampleRateIsLittleEndian()
    {
        var buffer = McapParser.CreateHeader(sampleCount: 1, sampleRate: 48000);

        var header = McapParser.ParseHeader(buffer);

        Assert.NotNull(header);
        Assert.Equal(48000u, header.SampleRate);

        // 48000 = 0x0000BB80
        Assert.Equal(0x80, buffer[12]); // Least significant
        Assert.Equal(0xBB, buffer[13]);
        Assert.Equal(0x00, buffer[14]);
        Assert.Equal(0x00, buffer[15]); // Most significant
    }

    [Fact]
    public void ParseHeader_TimestampIsLittleEndian()
    {
        ulong timestamp = 0x123456789ABCDEF0;
        var buffer = McapParser.CreateHeader(sampleCount: 1, timestamp: timestamp);

        var header = McapParser.ParseHeader(buffer);

        Assert.NotNull(header);
        Assert.Equal(timestamp, header.Timestamp);
    }

    [Fact]
    public void ParseHeader_MaxValues_Succeeds()
    {
        var buffer = McapParser.CreateHeader(
            sampleCount: uint.MaxValue,
            sampleRate: uint.MaxValue,
            bitsPerSample: byte.MaxValue,
            channels: byte.MaxValue,
            isFloat: byte.MaxValue,
            version: byte.MaxValue,
            timestamp: ulong.MaxValue);

        var header = McapParser.ParseHeader(buffer);

        Assert.NotNull(header);
        Assert.Equal(uint.MaxValue, header.SampleCount);
        Assert.Equal(uint.MaxValue, header.SampleRate);
        Assert.Equal(byte.MaxValue, header.BitsPerSample);
        Assert.Equal(byte.MaxValue, header.Channels);
        Assert.Equal(byte.MaxValue, header.IsFloat);
        Assert.Equal(byte.MaxValue, header.Version);
        Assert.Equal(ulong.MaxValue, header.Timestamp);
    }

    [Fact]
    public void ParseHeader_Span_WorksLikeArray()
    {
        var buffer = McapParser.CreateHeader(sampleCount: 999);

        var headerFromArray = McapParser.ParseHeader(buffer);
        var headerFromSpan = McapParser.ParseHeader(buffer.AsSpan());

        Assert.NotNull(headerFromArray);
        Assert.NotNull(headerFromSpan);
        Assert.Equal(headerFromArray.SampleCount, headerFromSpan.SampleCount);
    }

    #endregion

    #region ScanForMagic Tests

    [Fact]
    public void ScanForMagic_AtStart_ReturnsZero()
    {
        var buffer = McapParser.CreateHeader(sampleCount: 1);

        int offset = McapParser.ScanForMagic(buffer);

        Assert.Equal(0, offset);
    }

    [Fact]
    public void ScanForMagic_AtOffset10_Returns10()
    {
        var buffer = new byte[50];
        var header = McapParser.CreateHeader(sampleCount: 1);
        Array.Copy(header, 0, buffer, 10, 24);

        int offset = McapParser.ScanForMagic(buffer);

        Assert.Equal(10, offset);
    }

    [Fact]
    public void ScanForMagic_NotPresent_ReturnsNegativeOne()
    {
        var buffer = new byte[100]; // All zeros

        int offset = McapParser.ScanForMagic(buffer);

        Assert.Equal(-1, offset);
    }

    [Fact]
    public void ScanForMagic_WithStartOffset_SkipsEarlierOccurrence()
    {
        var buffer = new byte[60];
        var header = McapParser.CreateHeader(sampleCount: 1);
        // Put header at position 0 and position 30
        Array.Copy(header, 0, buffer, 0, 24);
        Array.Copy(header, 0, buffer, 30, 24);

        int offset = McapParser.ScanForMagic(buffer, startOffset: 5);

        Assert.Equal(30, offset);
    }

    [Fact]
    public void ScanForMagic_PartialMagic_DoesNotMatch()
    {
        var buffer = new byte[10];
        buffer[0] = 0x4D; // 'M'
        buffer[1] = 0x43; // 'C'
        buffer[2] = 0x41; // 'A'
        buffer[3] = 0x00; // Wrong - should be 'P'

        int offset = McapParser.ScanForMagic(buffer);

        Assert.Equal(-1, offset);
    }

    [Fact]
    public void ScanForMagic_BufferTooSmall_ReturnsNegativeOne()
    {
        var buffer = new byte[3]; // Less than 4 bytes needed for magic

        int offset = McapParser.ScanForMagic(buffer);

        Assert.Equal(-1, offset);
    }

    [Fact]
    public void ScanForMagic_ExactlyMagicSize_Succeeds()
    {
        var buffer = McapParser.MagicBytes.ToArray();

        int offset = McapParser.ScanForMagic(buffer);

        Assert.Equal(0, offset);
    }

    [Fact]
    public void ScanForMagic_MagicAtEnd_Succeeds()
    {
        var buffer = new byte[8];
        buffer[4] = 0x4D;
        buffer[5] = 0x43;
        buffer[6] = 0x41;
        buffer[7] = 0x50;

        int offset = McapParser.ScanForMagic(buffer);

        Assert.Equal(4, offset);
    }

    [Fact]
    public void ScanForMagic_StartOffsetBeyondEnd_ReturnsNegativeOne()
    {
        var buffer = McapParser.CreateHeader(sampleCount: 1);

        int offset = McapParser.ScanForMagic(buffer, startOffset: 100);

        Assert.Equal(-1, offset);
    }

    [Fact]
    public void ScanForMagic_Span_WorksLikeArray()
    {
        var buffer = new byte[30];
        var header = McapParser.CreateHeader(sampleCount: 1);
        Array.Copy(header, 0, buffer, 5, 24);

        int offsetFromArray = McapParser.ScanForMagic(buffer);
        int offsetFromSpan = McapParser.ScanForMagic(buffer.AsSpan());

        Assert.Equal(offsetFromArray, offsetFromSpan);
    }

    #endregion

    #region ValidateHeader Tests

    [Fact]
    public void ValidateHeader_ValidHeader_ReturnsTrue()
    {
        var header = new McapHeader
        {
            BitsPerSample = 16,
            Channels = 2,
            SampleRate = 48000,
            SampleCount = 480,
            IsFloat = 0
        };

        Assert.True(McapParser.ValidateHeader(header));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void ValidateHeader_ValidBitsPerSample_ReturnsTrue(byte bitsPerSample)
    {
        var header = new McapHeader
        {
            BitsPerSample = bitsPerSample,
            Channels = 2,
            SampleRate = 48000,
            SampleCount = 480,
            IsFloat = 0
        };

        Assert.True(McapParser.ValidateHeader(header));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(64)]
    public void ValidateHeader_InvalidBitsPerSample_ReturnsFalse(byte bitsPerSample)
    {
        var header = new McapHeader
        {
            BitsPerSample = bitsPerSample,
            Channels = 2,
            SampleRate = 48000,
            SampleCount = 480,
            IsFloat = 0
        };

        Assert.False(McapParser.ValidateHeader(header));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(8)]
    public void ValidateHeader_ValidChannels_ReturnsTrue(byte channels)
    {
        var header = new McapHeader
        {
            BitsPerSample = 16,
            Channels = channels,
            SampleRate = 48000,
            SampleCount = 480,
            IsFloat = 0
        };

        Assert.True(McapParser.ValidateHeader(header));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(255)]
    public void ValidateHeader_InvalidChannels_ReturnsFalse(byte channels)
    {
        var header = new McapHeader
        {
            BitsPerSample = 16,
            Channels = channels,
            SampleRate = 48000,
            SampleCount = 480,
            IsFloat = 0
        };

        Assert.False(McapParser.ValidateHeader(header));
    }

    [Theory]
    [InlineData(8000u)]
    [InlineData(16000u)]
    [InlineData(44100u)]
    [InlineData(48000u)]
    [InlineData(96000u)]
    public void ValidateHeader_ValidSampleRates_ReturnsTrue(uint sampleRate)
    {
        var header = new McapHeader
        {
            BitsPerSample = 16,
            Channels = 2,
            SampleRate = sampleRate,
            SampleCount = 480,
            IsFloat = 0
        };

        Assert.True(McapParser.ValidateHeader(header));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(7999u)]
    [InlineData(200000u)]
    public void ValidateHeader_InvalidSampleRates_ReturnsFalse(uint sampleRate)
    {
        var header = new McapHeader
        {
            BitsPerSample = 16,
            Channels = 2,
            SampleRate = sampleRate,
            SampleCount = 480,
            IsFloat = 0
        };

        Assert.False(McapParser.ValidateHeader(header));
    }

    [Fact]
    public void ValidateHeader_SampleCountTooLarge_ReturnsFalse()
    {
        var header = new McapHeader
        {
            BitsPerSample = 16,
            Channels = 2,
            SampleRate = 48000,
            SampleCount = 500000, // More than 10 seconds at 48kHz
            IsFloat = 0
        };

        Assert.False(McapParser.ValidateHeader(header));
    }

    [Fact]
    public void ValidateHeader_IsFloatGreaterThan1_ReturnsFalse()
    {
        var header = new McapHeader
        {
            BitsPerSample = 32,
            Channels = 2,
            SampleRate = 48000,
            SampleCount = 480,
            IsFloat = 2 // Should be 0 or 1
        };

        Assert.False(McapParser.ValidateHeader(header));
    }

    #endregion

    #region CreateHeader Tests

    [Fact]
    public void CreateHeader_DefaultValues_CreatesValidHeader()
    {
        var buffer = McapParser.CreateHeader(sampleCount: 480);

        Assert.Equal(24, buffer.Length);

        var header = McapParser.ParseHeader(buffer);
        Assert.NotNull(header);
        Assert.Equal(480u, header.SampleCount);
        Assert.Equal(48000u, header.SampleRate);
        Assert.Equal(16, header.BitsPerSample);
        Assert.Equal(2, header.Channels);
        Assert.Equal(0, header.IsFloat);
        Assert.Equal(1, header.Version);
        Assert.Equal(0ul, header.Timestamp);
    }

    [Fact]
    public void CreateHeader_Roundtrip_PreservesAllValues()
    {
        var original = new McapHeader
        {
            Version = 5,
            BitsPerSample = 24,
            Channels = 6,
            IsFloat = 1,
            SampleCount = 12345,
            SampleRate = 44100,
            Timestamp = 9876543210
        };

        var buffer = McapParser.CreateHeader(
            sampleCount: original.SampleCount,
            sampleRate: original.SampleRate,
            bitsPerSample: original.BitsPerSample,
            channels: original.Channels,
            isFloat: original.IsFloat,
            version: original.Version,
            timestamp: original.Timestamp);

        var parsed = McapParser.ParseHeader(buffer);

        Assert.NotNull(parsed);
        Assert.Equal(original.Version, parsed.Version);
        Assert.Equal(original.BitsPerSample, parsed.BitsPerSample);
        Assert.Equal(original.Channels, parsed.Channels);
        Assert.Equal(original.IsFloat, parsed.IsFloat);
        Assert.Equal(original.SampleCount, parsed.SampleCount);
        Assert.Equal(original.SampleRate, parsed.SampleRate);
        Assert.Equal(original.Timestamp, parsed.Timestamp);
    }

    #endregion

    #region McapHeader.AudioDataSize Tests

    [Fact]
    public void AudioDataSize_16BitStereo_CalculatesCorrectly()
    {
        var header = new McapHeader
        {
            SampleCount = 480,
            Channels = 2,
            BitsPerSample = 16
        };

        // 480 samples * 2 channels * 2 bytes = 1920 bytes
        Assert.Equal(1920, header.AudioDataSize);
    }

    [Fact]
    public void AudioDataSize_16BitMono_CalculatesCorrectly()
    {
        var header = new McapHeader
        {
            SampleCount = 480,
            Channels = 1,
            BitsPerSample = 16
        };

        // 480 samples * 1 channel * 2 bytes = 960 bytes
        Assert.Equal(960, header.AudioDataSize);
    }

    [Fact]
    public void AudioDataSize_32BitStereo_CalculatesCorrectly()
    {
        var header = new McapHeader
        {
            SampleCount = 480,
            Channels = 2,
            BitsPerSample = 32
        };

        // 480 samples * 2 channels * 4 bytes = 3840 bytes
        Assert.Equal(3840, header.AudioDataSize);
    }

    #endregion

    #region ScanForMagicAsync Tests

    [Fact]
    public async Task ScanForMagicAsync_MagicAtStart_FindsHeader()
    {
        var headerBytes = McapParser.CreateHeader(sampleCount: 100);
        using var stream = new MemoryStream(headerBytes);
        var buffer = new byte[24];

        bool found = await McapParser.ScanForMagicAsync(stream, buffer);

        Assert.True(found);
        var header = McapParser.ParseHeader(buffer);
        Assert.NotNull(header);
        Assert.Equal(100u, header.SampleCount);
    }

    [Fact]
    public async Task ScanForMagicAsync_MagicAfterGarbage_FindsHeader()
    {
        var garbage = new byte[50];
        var headerBytes = McapParser.CreateHeader(sampleCount: 200);
        var combined = new byte[garbage.Length + headerBytes.Length];
        Array.Copy(garbage, combined, garbage.Length);
        Array.Copy(headerBytes, 0, combined, garbage.Length, headerBytes.Length);

        using var stream = new MemoryStream(combined);
        var buffer = new byte[24];

        bool found = await McapParser.ScanForMagicAsync(stream, buffer);

        Assert.True(found);
        var header = McapParser.ParseHeader(buffer);
        Assert.NotNull(header);
        Assert.Equal(200u, header.SampleCount);
    }

    [Fact]
    public async Task ScanForMagicAsync_NoMagic_ReturnsFalse()
    {
        var garbage = new byte[100]; // No magic bytes
        using var stream = new MemoryStream(garbage);
        var buffer = new byte[24];

        bool found = await McapParser.ScanForMagicAsync(stream, buffer);

        Assert.False(found);
    }

    [Fact]
    public async Task ScanForMagicAsync_EmptyStream_ReturnsFalse()
    {
        using var stream = new MemoryStream();
        var buffer = new byte[24];

        bool found = await McapParser.ScanForMagicAsync(stream, buffer);

        Assert.False(found);
    }

    [Fact]
    public async Task ScanForMagicAsync_PartialMagic_SkipsToRealMagic()
    {
        // Create a stream with partial magic followed by real header
        var data = new byte[50];
        data[0] = 0x4D; // 'M'
        data[1] = 0x43; // 'C'
        data[2] = 0x41; // 'A'
        data[3] = 0x00; // Wrong - not 'P'

        var headerBytes = McapParser.CreateHeader(sampleCount: 300);
        Array.Copy(headerBytes, 0, data, 20, 24);

        using var stream = new MemoryStream(data);
        var buffer = new byte[24];

        bool found = await McapParser.ScanForMagicAsync(stream, buffer);

        Assert.True(found);
        var header = McapParser.ParseHeader(buffer);
        Assert.NotNull(header);
        Assert.Equal(300u, header.SampleCount);
    }

    [Fact]
    public async Task ScanForMagicAsync_CancellationRequested_ReturnsFalse()
    {
        var garbage = new byte[1000];
        using var stream = new MemoryStream(garbage);
        var buffer = new byte[24];
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        bool found = await McapParser.ScanForMagicAsync(stream, buffer, cts.Token);

        Assert.False(found);
    }

    [Fact]
    public async Task ScanForMagicAsync_BufferTooSmall_ThrowsArgumentException()
    {
        using var stream = new MemoryStream(McapParser.CreateHeader(sampleCount: 1));
        var buffer = new byte[23]; // One byte too small

        await Assert.ThrowsAsync<ArgumentException>(() =>
            McapParser.ScanForMagicAsync(stream, buffer));
    }

    #endregion
}
