using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Snacka.Server.Services;

namespace Snacka.Server.Tests.Services;

/// <summary>Real images through the avatar pipeline (guards ImageSharp upgrades, see #12).</summary>
[TestClass]
public class ImageProcessingServiceTests
{
    private string _dir = "";
    private ImageProcessingService _service = null!;

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

    private static MemoryStream MakeImage(int width, int height, Action<Image, Stream> save)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(200, 40, 90));
        var stream = new MemoryStream();
        save(image, stream);
        stream.Position = 0;
        return stream;
    }

    [DataTestMethod]
    [DataRow("png")]
    [DataRow("jpeg")]
    [DataRow("gif")]
    [DataRow("webp")]
    public async Task ProcessAvatar_AcceptsCommonFormats_AndCanBeReadBack(string format)
    {
        using var source = MakeImage(640, 480, format switch
        {
            "png" => (img, s) => img.SaveAsPng(s),
            "jpeg" => (img, s) => img.SaveAsJpeg(s),
            "gif" => (img, s) => img.SaveAsGif(s),
            _ => (img, s) => img.SaveAsWebp(s),
        });

        var result = await _service.ProcessAvatarAsync(source, cropRegion: null);

        Assert.IsTrue(result.Success, result.ErrorMessage);
        var file = await _service.GetAvatarAsync(result.FileName);
        Assert.IsNotNull(file);
        using var decoded = await Image.LoadAsync(file.Stream);
        Assert.IsTrue(decoded.Width > 0 && decoded.Height > 0);
        file.Stream.Dispose();
    }

    [TestMethod]
    public async Task ProcessAvatar_WithCrop_Succeeds()
    {
        using var source = MakeImage(800, 600, (img, s) => img.SaveAsPng(s));

        var result = await _service.ProcessAvatarAsync(source, new CropRegion(100, 50, 300, 300));

        Assert.IsTrue(result.Success, result.ErrorMessage);
    }

    [TestMethod]
    public async Task ProcessAvatar_RejectsGarbage()
    {
        using var source = new MemoryStream("definitely not an image"u8.ToArray());

        var result = await _service.ProcessAvatarAsync(source, cropRegion: null);

        Assert.IsFalse(result.Success);
    }
}
