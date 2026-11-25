using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using Snacka.Server.Services;

namespace Snacka.Server.Tests.Services;

/// <summary>Real images through the avatar pipeline (guards image library changes, see #12).</summary>
[TestClass]
public class ImageProcessingServiceTests
{
    private string _dir = "";
    private ImageProcessingService _service = null!;

    /// <summary>A 1x1 GIF (Skia decodes GIF but can't encode it).</summary>
    private static readonly byte[] TinyGif = Convert.FromBase64String("R0lGODlhAQABAIAAAP///wAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==");

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "snacka-img-" + Guid.NewGuid());
        _service = new ImageProcessingService(
            Options.Create(new FileStorageSettings { BasePath = _dir }),
            NullLogger<ImageProcessingService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A solid-color test image, left half red and right half blue, encoded as <paramref name="format"/>.</summary>
    private static MemoryStream MakeImage(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Red);
            using var blue = new SKPaint { Color = SKColors.Blue };
            canvas.DrawRect(width / 2f, 0, width / 2f, height, blue);
        }
        using var data = bitmap.Encode(format, 90);
        return new MemoryStream(data.ToArray());
    }

    private async Task<SKBitmap> ReadBackAsync(AvatarProcessResult result)
    {
        Assert.IsTrue(result.Success, result.ErrorMessage);
        var file = await _service.GetAvatarAsync(result.FileName);
        Assert.IsNotNull(file);
        using var stream = file.Stream;
        return SKBitmap.Decode(stream);
    }

    [DataTestMethod]
    [DataRow("png")]
    [DataRow("jpeg")]
    [DataRow("webp")]
    [DataRow("gif")]
    public async Task ProcessAvatar_AcceptsCommonFormats_AndProducesASquarePng(string format)
    {
        using var source = format switch
        {
            "png" => MakeImage(640, 480, SKEncodedImageFormat.Png),
            "jpeg" => MakeImage(640, 480, SKEncodedImageFormat.Jpeg),
            "webp" => MakeImage(640, 480, SKEncodedImageFormat.Webp),
            _ => new MemoryStream(TinyGif),
        };

        var result = await _service.ProcessAvatarAsync(source, cropRegion: null);

        using var avatar = await ReadBackAsync(result);
        var size = new FileStorageSettings().AvatarSize;
        Assert.AreEqual(size, avatar.Width);
        Assert.AreEqual(size, avatar.Height);
        Assert.IsTrue(result.FileName.EndsWith(".png"));
    }

    [TestMethod]
    public async Task ProcessAvatar_CoverResize_KeepsTheCenter()
    {
        // 640x480, red left half, blue right half: the centered 480x480 square is half and half.
        using var source = MakeImage(640, 480, SKEncodedImageFormat.Png);

        using var avatar = await ReadBackAsync(await _service.ProcessAvatarAsync(source, cropRegion: null));

        Assert.AreEqual(SKColors.Red, avatar.GetPixel(2, avatar.Height / 2));
        Assert.AreEqual(SKColors.Blue, avatar.GetPixel(avatar.Width - 3, avatar.Height / 2));
    }

    [TestMethod]
    public async Task ProcessAvatar_WithCrop_UsesTheCroppedRegion()
    {
        // Crop to the right quarter only (all blue).
        using var source = MakeImage(800, 600, SKEncodedImageFormat.Png);

        using var avatar = await ReadBackAsync(await _service.ProcessAvatarAsync(source, new CropRegion(0.75, 0, 0.25, 1)));

        Assert.AreEqual(SKColors.Blue, avatar.GetPixel(avatar.Width / 2, avatar.Height / 2));
        Assert.AreEqual(SKColors.Blue, avatar.GetPixel(1, 1));
    }

    [TestMethod]
    public async Task ProcessAvatar_RejectsGarbage()
    {
        using var source = new MemoryStream("definitely not an image"u8.ToArray());

        var result = await _service.ProcessAvatarAsync(source, cropRegion: null);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task ProcessAvatar_RejectsHugeDimensions_BeforeDecoding()
    {
        // A tiny PNG header claiming 20000x20000 pixels (a decompression bomb) must be refused.
        using var source = MakeImage(20000, 1, SKEncodedImageFormat.Png);
        var bytes = source.ToArray();
        // IHDR height is big-endian at offset 20; its CRC (over type + data, bytes 12..28) follows at 29.
        bytes[20] = 0; bytes[21] = 0; bytes[22] = 0x4E; bytes[23] = 0x20;
        var crc = Crc32(bytes.AsSpan(12, 17));
        bytes[29] = (byte)(crc >> 24); bytes[30] = (byte)(crc >> 16); bytes[31] = (byte)(crc >> 8); bytes[32] = (byte)crc;

        var result = await _service.ProcessAvatarAsync(new MemoryStream(bytes), cropRegion: null);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("Image dimensions are too large", result.ErrorMessage);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
