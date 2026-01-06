using SIPSorceryMedia.Abstractions;
using Miscord.Client.Services;

Console.WriteLine("Video Encoder Test Tool");
Console.WriteLine("=======================");

int width = 640;
int height = 480;
var bgrData = CreateTestFrameBgr(width, height);
Console.WriteLine($"Created test frame: {width}x{height}, {bgrData.Length} bytes (BGR)");

// Test FfmpegProcessEncoder
Console.WriteLine("\nTesting FfmpegProcessEncoder (H264)...");
try
{
    int encodedCount = 0;
    var encoder = new FfmpegProcessEncoder(width, height, 15, VideoCodecsEnum.H264);

    encoder.OnEncodedFrame += (durationRtpUnits, data) =>
    {
        encodedCount++;
        if (encodedCount <= 5 || encodedCount % 10 == 0)
        {
            Console.WriteLine($"  Received encoded data: {data.Length} bytes (frame {encodedCount}, duration: {durationRtpUnits})");
        }
    };

    encoder.Start();
    Console.WriteLine("Encoder started, sending 30 frames...");

    // Send 30 frames
    for (int i = 0; i < 30; i++)
    {
        encoder.EncodeFrame(bgrData);
        Thread.Sleep(66); // ~15fps
    }

    // Wait a bit for encoding to complete
    Thread.Sleep(500);

    Console.WriteLine($"  Total encoded frames received: {encodedCount}");
    encoder.Dispose();

    if (encodedCount > 0)
    {
        Console.WriteLine("  SUCCESS: FfmpegProcessEncoder works!");
    }
    else
    {
        Console.WriteLine("  WARNING: No encoded frames received");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"  FAILED: {ex.Message}");
}

Console.WriteLine("\nDone.");

static byte[] CreateTestFrameBgr(int width, int height)
{
    var data = new byte[width * height * 3];

    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            int idx = (y * width + x) * 3;
            data[idx] = (byte)(x * 255 / width);     // B
            data[idx + 1] = (byte)(y * 255 / height); // G
            data[idx + 2] = 128;                      // R
        }
    }

    return data;
}
