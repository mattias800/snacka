using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace Snacka.Client.Tests;

/// <summary>
/// Tests for audio format handling to verify correct format selection.
/// These tests verify that the OPUS format (48kHz) is properly selected
/// to prevent the "heavily pitched down" microphone issue.
/// </summary>
public class AudioFormatTests
{
    /// <summary>
    /// Verifies that AudioEncoder with Opus enabled includes the OPUS format.
    /// </summary>
    [Fact]
    public void AudioEncoder_WithOpus_IncludesOpusFormat()
    {
        var encoder = new AudioEncoder(includeOpus: true);
        var formats = encoder.SupportedFormats;

        var opusFormat = formats.FirstOrDefault(f => f.FormatName == "OPUS");

        Assert.NotNull(opusFormat.FormatName);
        Assert.Equal("OPUS", opusFormat.FormatName);
        Assert.Equal(48000, opusFormat.ClockRate);
    }

    /// <summary>
    /// Verifies that PCMU format (G.711) uses 8kHz.
    /// </summary>
    [Fact]
    public void AudioEncoder_PcmuFormat_Is8kHz()
    {
        var encoder = new AudioEncoder(includeOpus: true);
        var formats = encoder.SupportedFormats;

        var pcmuFormat = formats.FirstOrDefault(f => f.FormatName == "PCMU");

        Assert.NotNull(pcmuFormat.FormatName);
        Assert.Equal("PCMU", pcmuFormat.FormatName);
        Assert.Equal(8000, pcmuFormat.ClockRate);
    }

    /// <summary>
    /// This test verifies the core bug scenario: if formats[0] is PCMU (8kHz)
    /// and we use that instead of OPUS (48kHz), we get a 6x sample rate mismatch.
    /// </summary>
    [Fact]
    public void AudioEncoder_FirstFormat_IsNotAlwaysOpus()
    {
        var encoder = new AudioEncoder(includeOpus: true);
        var formats = encoder.SupportedFormats;

        // The first format might be PCMU (8kHz), not OPUS (48kHz)
        // This is why we need to explicitly select OPUS, not just use formats[0]
        var firstFormat = formats[0];
        var opusFormat = formats.FirstOrDefault(f => f.FormatName == "OPUS");

        // If the first format is not OPUS, using formats[0] would cause pitch issues
        if (firstFormat.FormatName != "OPUS")
        {
            // First format is not OPUS - this is the bug scenario
            Assert.NotEqual(opusFormat.ClockRate, firstFormat.ClockRate);

            // Calculate the pitch ratio (would cause audio to be pitched down)
            double pitchRatio = (double)firstFormat.ClockRate / opusFormat.ClockRate;
            Assert.True(pitchRatio < 1, $"First format {firstFormat.FormatName} ({firstFormat.ClockRate}Hz) would cause pitch down when OPUS ({opusFormat.ClockRate}Hz) audio is played on it");
        }
    }

    /// <summary>
    /// Verifies correct format selection logic that should be used in audio setup code.
    /// This mirrors the fix in AudioDeviceService.SetLoopbackEnabled.
    /// </summary>
    [Fact]
    public void FormatSelection_ShouldPreferOpus()
    {
        var encoder = new AudioEncoder(includeOpus: true);
        var formats = encoder.SupportedFormats.ToList();

        // Correct format selection logic (from the fix)
        var selectedFormat = formats.FirstOrDefault(f => f.FormatName == "OPUS");
        if (selectedFormat.FormatName == null)
            selectedFormat = formats.FirstOrDefault(f => f.FormatName == "PCMU");
        if (selectedFormat.FormatName == null)
            selectedFormat = formats[0];

        // Should select OPUS
        Assert.Equal("OPUS", selectedFormat.FormatName);
        Assert.Equal(48000, selectedFormat.ClockRate);
    }

    /// <summary>
    /// Verifies that using formats[0] directly (the bug) vs proper selection (the fix)
    /// can result in different formats.
    /// </summary>
    [Fact]
    public void FormatSelection_BugVsFix_Difference()
    {
        var encoder = new AudioEncoder(includeOpus: true);
        var formats = encoder.SupportedFormats.ToList();

        // Bug: use formats[0] directly
        var buggyFormat = formats[0];

        // Fix: prefer OPUS
        var fixedFormat = formats.FirstOrDefault(f => f.FormatName == "OPUS");
        if (fixedFormat.FormatName == null)
            fixedFormat = formats.FirstOrDefault(f => f.FormatName == "PCMU");
        if (fixedFormat.FormatName == null)
            fixedFormat = formats[0];

        // If formats[0] is not OPUS, the buggy selection would cause issues
        if (buggyFormat.FormatName != "OPUS")
        {
            Assert.NotEqual(buggyFormat.FormatName, fixedFormat.FormatName);
            Assert.NotEqual(buggyFormat.ClockRate, fixedFormat.ClockRate);
        }
    }

    /// <summary>
    /// Verifies that when source uses OPUS and sink uses formats[0],
    /// there's a potential sample rate mismatch.
    /// This is the exact bug that was fixed in AudioDeviceService.
    /// </summary>
    [Fact]
    public void SourceSinkMismatch_CausesPitchIssue()
    {
        var encoder = new AudioEncoder(includeOpus: true);
        var formats = encoder.SupportedFormats.ToList();

        // Source format: properly selected OPUS
        var sourceFormat = formats.FirstOrDefault(f => f.FormatName == "OPUS");
        Assert.NotNull(sourceFormat.FormatName);

        // Buggy sink format: just use formats[0]
        var buggySinkFormat = formats[0];

        // Calculate if there would be a sample rate mismatch
        if (sourceFormat.ClockRate != buggySinkFormat.ClockRate)
        {
            // This is the bug: source at 48kHz, sink at 8kHz
            // Results in 6x slower playback (heavily pitched down)
            double playbackRatio = (double)buggySinkFormat.ClockRate / sourceFormat.ClockRate;

            Assert.True(playbackRatio < 1,
                $"Sample rate mismatch detected: Source={sourceFormat.ClockRate}Hz ({sourceFormat.FormatName}), " +
                $"Sink={buggySinkFormat.ClockRate}Hz ({buggySinkFormat.FormatName}). " +
                $"Playback would be {1 / playbackRatio:F1}x slower (heavily pitched down)");
        }
    }
}
