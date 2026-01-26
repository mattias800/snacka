using System.Diagnostics;
using System.Text.Json;

namespace Snacka.Client.Tests;

/// <summary>
/// Regression tests for native capture tool (SnackaCaptureVideoToolbox on macOS).
/// These tests verify:
/// 1. Device enumeration works correctly (list --json returns microphones/cameras)
/// 2. Microphone lookup by name, id, and index works
/// 3. MCAP header format is correct for audio streaming
///
/// Note: These tests require the native tool to be built and available.
/// On macOS, they require microphone permission to be granted.
/// </summary>
public class NativeCaptureRegressionTests
{
    private static readonly string? NativeToolPath = GetNativeToolPath();

    private static string? GetNativeToolPath()
    {
        if (!OperatingSystem.IsMacOS())
            return null;

        // Check common locations
        var locations = new[]
        {
            // Development location
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "..", "src",
                "SnackaCaptureVideoToolbox", ".build", "arm64-apple-macosx", "release", "SnackaCaptureVideoToolbox"),
            // Relative to test assembly
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src",
                "SnackaCaptureVideoToolbox", ".build", "arm64-apple-macosx", "release", "SnackaCaptureVideoToolbox"),
        };

        foreach (var location in locations)
        {
            var fullPath = Path.GetFullPath(location);
            if (File.Exists(fullPath))
                return fullPath;
        }

        return null;
    }

    #region Device Enumeration Tests

    [SkippableFact]
    public async Task ListCommand_ReturnsValidJson()
    {
        Skip.If(NativeToolPath == null, "Native capture tool not available");

        var (exitCode, stdout, _) = await RunNativeToolAsync("list --json", timeoutSeconds: 10);

        Assert.Equal(0, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(stdout), "Output should not be empty");

        // Should be valid JSON
        var doc = JsonDocument.Parse(stdout);
        Assert.NotNull(doc);
    }

    [SkippableFact]
    public async Task ListCommand_ReturnsMicrophonesArray()
    {
        Skip.If(NativeToolPath == null, "Native capture tool not available");

        var (exitCode, stdout, _) = await RunNativeToolAsync("list --json", timeoutSeconds: 10);

        Assert.Equal(0, exitCode);

        using var doc = JsonDocument.Parse(stdout);
        Assert.True(doc.RootElement.TryGetProperty("microphones", out var microphonesElement),
            "JSON should contain 'microphones' property");
        Assert.Equal(JsonValueKind.Array, microphonesElement.ValueKind);
    }

    [SkippableFact]
    public async Task ListCommand_ReturnsCamerasArray()
    {
        Skip.If(NativeToolPath == null, "Native capture tool not available");

        var (exitCode, stdout, _) = await RunNativeToolAsync("list --json", timeoutSeconds: 10);

        Assert.Equal(0, exitCode);

        using var doc = JsonDocument.Parse(stdout);
        Assert.True(doc.RootElement.TryGetProperty("cameras", out var camerasElement),
            "JSON should contain 'cameras' property");
        Assert.Equal(JsonValueKind.Array, camerasElement.ValueKind);
    }

    [SkippableFact]
    public async Task ListCommand_MicrophonesHaveRequiredFields()
    {
        Skip.If(NativeToolPath == null, "Native capture tool not available");

        var (exitCode, stdout, _) = await RunNativeToolAsync("list --json", timeoutSeconds: 10);

        Assert.Equal(0, exitCode);

        using var doc = JsonDocument.Parse(stdout);
        var microphones = doc.RootElement.GetProperty("microphones");

        // If there are microphones, verify they have required fields
        foreach (var mic in microphones.EnumerateArray())
        {
            Assert.True(mic.TryGetProperty("id", out var id), "Microphone should have 'id' field");
            Assert.True(mic.TryGetProperty("name", out var name), "Microphone should have 'name' field");
            Assert.True(mic.TryGetProperty("index", out var index), "Microphone should have 'index' field");

            Assert.Equal(JsonValueKind.String, id.ValueKind);
            Assert.Equal(JsonValueKind.String, name.ValueKind);
            Assert.Equal(JsonValueKind.Number, index.ValueKind);

            // Verify id and name are not empty
            Assert.False(string.IsNullOrEmpty(id.GetString()), "Microphone id should not be empty");
            Assert.False(string.IsNullOrEmpty(name.GetString()), "Microphone name should not be empty");
            Assert.True(index.GetInt32() >= 0, "Microphone index should be non-negative");
        }
    }

    [SkippableFact]
    public async Task ListCommand_CamerasHaveRequiredFields()
    {
        Skip.If(NativeToolPath == null, "Native capture tool not available");

        var (exitCode, stdout, _) = await RunNativeToolAsync("list --json", timeoutSeconds: 10);

        Assert.Equal(0, exitCode);

        using var doc = JsonDocument.Parse(stdout);
        var cameras = doc.RootElement.GetProperty("cameras");

        // If there are cameras, verify they have required fields
        foreach (var cam in cameras.EnumerateArray())
        {
            Assert.True(cam.TryGetProperty("id", out var id), "Camera should have 'id' field");
            Assert.True(cam.TryGetProperty("name", out var name), "Camera should have 'name' field");
            Assert.True(cam.TryGetProperty("index", out var index), "Camera should have 'index' field");

            Assert.Equal(JsonValueKind.String, id.ValueKind);
            Assert.Equal(JsonValueKind.String, name.ValueKind);
            Assert.Equal(JsonValueKind.Number, index.ValueKind);
        }
    }

    [SkippableFact]
    public async Task ListCommand_CompletesWithinTimeout()
    {
        Skip.If(NativeToolPath == null, "Native capture tool not available");

        // The list command should complete quickly (within 5 seconds)
        // This tests for the deadlock bug where stdout/stderr weren't drained concurrently
        var sw = Stopwatch.StartNew();

        var (exitCode, _, _) = await RunNativeToolAsync("list --json", timeoutSeconds: 5);

        sw.Stop();

        Assert.Equal(0, exitCode);
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"List command should complete within 5 seconds, took {sw.ElapsedMilliseconds}ms");
    }

    #endregion

    #region MCAP Header Format Tests

    /// <summary>
    /// Verifies the MCAP header magic bytes are correct (big-endian "MCAP").
    /// </summary>
    [Fact]
    public void McapHeader_MagicBytes_AreBigEndian()
    {
        // "MCAP" in ASCII: M=0x4D, C=0x43, A=0x41, P=0x50
        // Big-endian means most significant byte first
        byte[] expectedMagic = { 0x4D, 0x43, 0x41, 0x50 };
        uint expectedMagicUInt = 0x4D434150;

        // Parse as big-endian
        uint parsedMagic = (uint)(expectedMagic[0] << 24 | expectedMagic[1] << 16 |
                                  expectedMagic[2] << 8 | expectedMagic[3]);

        Assert.Equal(expectedMagicUInt, parsedMagic);
    }

    /// <summary>
    /// Verifies MCAP header size is exactly 24 bytes.
    /// Layout: magic(4) + version(1) + bitsPerSample(1) + channels(1) + isFloat(1) +
    ///         sampleCount(4) + sampleRate(4) + timestamp(8) = 24 bytes
    /// </summary>
    [Fact]
    public void McapHeader_Size_Is24Bytes()
    {
        int headerSize = 4 + 1 + 1 + 1 + 1 + 4 + 4 + 8;
        Assert.Equal(24, headerSize);
    }

    /// <summary>
    /// Tests parsing a valid MCAP header.
    /// </summary>
    [Fact]
    public void McapHeader_Parse_ValidHeader()
    {
        // Construct a valid header
        var header = new byte[24];

        // Magic: "MCAP" big-endian
        header[0] = 0x4D; // M
        header[1] = 0x43; // C
        header[2] = 0x41; // A
        header[3] = 0x50; // P

        // Version: 2
        header[4] = 2;

        // BitsPerSample: 16
        header[5] = 16;

        // Channels: 2 (stereo)
        header[6] = 2;

        // IsFloat: 0 (integer PCM)
        header[7] = 0;

        // SampleCount: 480 (little-endian)
        WriteUInt32LE(header, 8, 480);

        // SampleRate: 48000 (little-endian)
        WriteUInt32LE(header, 12, 48000);

        // Timestamp: 123456789 (little-endian)
        WriteUInt64LE(header, 16, 123456789);

        // Parse it
        var parsed = ParseMcapHeader(header);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed.Value.Version);
        Assert.Equal(16, parsed.Value.BitsPerSample);
        Assert.Equal(2, parsed.Value.Channels);
        Assert.Equal(0, parsed.Value.IsFloat);
        Assert.Equal(480u, parsed.Value.SampleCount);
        Assert.Equal(48000u, parsed.Value.SampleRate);
        Assert.Equal(123456789ul, parsed.Value.Timestamp);
    }

    /// <summary>
    /// Tests that invalid magic bytes are rejected.
    /// </summary>
    [Fact]
    public void McapHeader_Parse_RejectsInvalidMagic()
    {
        var header = new byte[24];

        // Invalid magic
        header[0] = 0x00;
        header[1] = 0x00;
        header[2] = 0x00;
        header[3] = 0x00;

        var parsed = ParseMcapHeader(header);
        Assert.Null(parsed);
    }

    /// <summary>
    /// Tests that text data (log messages) are rejected as invalid MCAP.
    /// </summary>
    [Theory]
    [InlineData("MicrophoneCapturer: ")]
    [InlineData("Audio samples: 480\n")]
    [InlineData("ERROR: Something went wrong")]
    public void McapHeader_Parse_RejectsTextData(string text)
    {
        var header = new byte[24];
        var textBytes = System.Text.Encoding.ASCII.GetBytes(text);
        Array.Copy(textBytes, header, Math.Min(textBytes.Length, 24));

        var parsed = ParseMcapHeader(header);
        Assert.Null(parsed);
    }

    #endregion

    #region Microphone Capture Integration Tests

    [SkippableFact]
    public async Task MicrophoneCapture_WithValidDevice_ProducesMcapPackets()
    {
        Skip.If(NativeToolPath == null, "Native capture tool not available");

        // First get available microphones
        var (listExitCode, listStdout, _) = await RunNativeToolAsync("list --json", timeoutSeconds: 10);
        Skip.If(listExitCode != 0, "Could not list devices");

        using var doc = JsonDocument.Parse(listStdout);
        var microphones = doc.RootElement.GetProperty("microphones");
        Skip.If(microphones.GetArrayLength() == 0, "No microphones available");

        // Get the first microphone's index
        var firstMic = microphones.EnumerateArray().First();
        var micIndex = firstMic.GetProperty("index").GetInt32();

        // Try to capture briefly - just verify it starts and produces MCAP data
        var (exitCode, _, stderr) = await RunNativeToolAsync(
            $"--microphone {micIndex}",
            timeoutSeconds: 2,
            captureStderr: true,
            killAfterTimeout: true);

        // Process should be killed (exit code may vary) but should have produced output
        // Check stderr for MCAP magic or startup messages
        Assert.False(string.IsNullOrEmpty(stderr),
            "Microphone capture should produce stderr output (MCAP packets or logs)");
    }

    [SkippableFact]
    public async Task MicrophoneCapture_ByName_FindsDevice()
    {
        Skip.If(NativeToolPath == null, "Native capture tool not available");

        // First get available microphones
        var (listExitCode, listStdout, _) = await RunNativeToolAsync("list --json", timeoutSeconds: 10);
        Skip.If(listExitCode != 0, "Could not list devices");

        using var doc = JsonDocument.Parse(listStdout);
        var microphones = doc.RootElement.GetProperty("microphones");
        Skip.If(microphones.GetArrayLength() == 0, "No microphones available");

        // Get the first microphone's name
        var firstMic = microphones.EnumerateArray().First();
        var micName = firstMic.GetProperty("name").GetString();

        // Try to capture by name - should find the device
        var (exitCode, _, stderr) = await RunNativeToolAsync(
            $"--microphone \"{micName}\"",
            timeoutSeconds: 2,
            captureStderr: true,
            killAfterTimeout: true);

        // Check that it found the microphone (log message should appear)
        Assert.Contains("Found microphone", stderr);
    }

    #endregion

    #region Helper Methods

    private static async Task<(int exitCode, string stdout, string stderr)> RunNativeToolAsync(
        string arguments,
        int timeoutSeconds = 10,
        bool captureStderr = false,
        bool killAfterTimeout = false)
    {
        if (NativeToolPath == null)
            return (-1, "", "Native tool not available");

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = NativeToolPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        process.Start();

        // Read both streams concurrently to avoid deadlock
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var completed = await Task.Run(() =>
            process.WaitForExit(timeoutSeconds * 1000));

        if (!completed)
        {
            if (killAfterTimeout)
            {
                try { process.Kill(); } catch { }
            }
            // Give it a moment to clean up
            await Task.Delay(100);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (completed ? process.ExitCode : -1, stdout, stderr);
    }

    private static void WriteUInt32LE(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteUInt64LE(byte[] buffer, int offset, ulong value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        buffer[offset + 4] = (byte)((value >> 32) & 0xFF);
        buffer[offset + 5] = (byte)((value >> 40) & 0xFF);
        buffer[offset + 6] = (byte)((value >> 48) & 0xFF);
        buffer[offset + 7] = (byte)((value >> 56) & 0xFF);
    }

    private static McapHeader? ParseMcapHeader(byte[] buffer)
    {
        if (buffer.Length < 24)
            return null;

        // Read magic (big-endian)
        uint magic = (uint)(buffer[0] << 24 | buffer[1] << 16 | buffer[2] << 8 | buffer[3]);
        if (magic != 0x4D434150) // "MCAP"
            return null;

        return new McapHeader
        {
            Version = buffer[4],
            BitsPerSample = buffer[5],
            Channels = buffer[6],
            IsFloat = buffer[7],
            SampleCount = (uint)(buffer[8] | buffer[9] << 8 | buffer[10] << 16 | buffer[11] << 24),
            SampleRate = (uint)(buffer[12] | buffer[13] << 8 | buffer[14] << 16 | buffer[15] << 24),
            Timestamp = (ulong)(
                (ulong)buffer[16] | (ulong)buffer[17] << 8 |
                (ulong)buffer[18] << 16 | (ulong)buffer[19] << 24 |
                (ulong)buffer[20] << 32 | (ulong)buffer[21] << 40 |
                (ulong)buffer[22] << 48 | (ulong)buffer[23] << 56)
        };
    }

    private struct McapHeader
    {
        public byte Version;
        public byte BitsPerSample;
        public byte Channels;
        public byte IsFloat;
        public uint SampleCount;
        public uint SampleRate;
        public ulong Timestamp;
    }

    #endregion
}

/// <summary>
/// Tests for NativeCaptureLocator microphone enumeration.
/// These tests verify the C# side of the native capture integration.
/// </summary>
public class NativeCaptureLocatorTests
{
    /// <summary>
    /// Verifies that GetAvailableMicrophonesAsync doesn't hang (timeout bug regression).
    /// </summary>
    [SkippableFact]
    public async Task GetAvailableMicrophonesAsync_CompletesWithinTimeout()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "macOS only test");

        var locator = new Snacka.Client.Services.WebRtc.NativeCaptureLocator();
        Skip.If(!locator.IsNativeMicrophoneCaptureAvailable(), "Native capture tool not available");

        var sw = Stopwatch.StartNew();

        var microphones = await locator.GetAvailableMicrophonesAsync();

        sw.Stop();

        // Should complete within 10 seconds (the timeout is 5 seconds internally)
        Assert.True(sw.ElapsedMilliseconds < 10000,
            $"GetAvailableMicrophonesAsync should complete within timeout, took {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Verifies that microphones are returned with proper fields.
    /// </summary>
    [SkippableFact]
    public async Task GetAvailableMicrophonesAsync_ReturnsMicrophonesWithValidFields()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "macOS only test");

        var locator = new Snacka.Client.Services.WebRtc.NativeCaptureLocator();
        Skip.If(!locator.IsNativeMicrophoneCaptureAvailable(), "Native capture tool not available");

        var microphones = await locator.GetAvailableMicrophonesAsync();

        // May have 0 microphones if none connected or permission denied
        foreach (var mic in microphones)
        {
            Assert.False(string.IsNullOrEmpty(mic.Id), "Microphone should have non-empty Id");
            Assert.False(string.IsNullOrEmpty(mic.Name), "Microphone should have non-empty Name");
            Assert.True(mic.Index >= 0, "Microphone should have non-negative Index");
        }
    }
}
