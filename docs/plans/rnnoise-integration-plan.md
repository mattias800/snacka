# Implementation Plan: RNNoise Noise Suppression

## Overview

Integrate Mozilla's RNNoise library into all native capture tools to provide ML-based noise suppression for microphone input. Users can toggle this feature in the Audio Settings UI.

---

## Part 1: RNNoise Library Setup

### 1.1 Add RNNoise as a Submodule/Dependency

RNNoise source: https://github.com/xiph/rnnoise (BSD-3-Clause)

**Files needed per platform:**
- Source files: `src/rnn.c`, `src/rnn_data.c`, `src/denoise.c`, etc.
- Header: `include/rnnoise.h`
- Trained model weights are embedded in `rnn_data.c`

### 1.2 Build Integration

**macOS (SnackaCaptureVideoToolbox):**
- Add RNNoise C sources to the Swift Package
- Create a C bridging header to expose `rnnoise_create()`, `rnnoise_process_frame()`, `rnnoise_destroy()`
- Link as part of the Swift build

**Windows (SnackaCaptureWindows):**
- Add RNNoise sources to CMakeLists.txt
- Compile as part of the executable

**Linux (SnackaCaptureLinux):**
- Add RNNoise sources to CMakeLists.txt
- Compile as part of the executable

---

## Part 2: Native Capture Tool Changes

### 2.1 CLI Arguments

Add to all platforms:

| Argument | Description |
|----------|-------------|
| `--noise-suppression` | Enable noise suppression (default) |
| `--no-noise-suppression` | Disable noise suppression |

### 2.2 macOS Implementation

**Modify: `SnackaCaptureApp.swift`**
```swift
@Flag(name: .long, help: "Enable noise suppression (default: true)")
var noiseSuppression: Bool = true

@Flag(name: .long, inversion: .prefixedNo, help: "Disable noise suppression")
var noNoiseSuppression: Bool = false
```

**Modify: `MicrophoneCapturer.swift`**
- Add RNNoise state initialization
- Process audio through RNNoise before outputting MCAP packets
- RNNoise works on 10ms frames (480 samples at 48kHz), mono
- Process left and right channels separately, then interleave back to stereo

```swift
// Pseudocode
class MicrophoneCapturer {
    private var rnnoiseStateLeft: OpaquePointer?
    private var rnnoiseStateRight: OpaquePointer?
    private var noiseSuppressionEnabled: Bool

    func processAudio(samples: [Int16]) -> [Int16] {
        guard noiseSuppressionEnabled else { return samples }

        // Split stereo to mono channels
        // Convert Int16 to Float32 (RNNoise expects -1.0 to 1.0 scaled to -32768 to 32767)
        // Process 480-sample frames through rnnoise_process_frame()
        // Convert back and interleave to stereo
    }
}
```

**New file: `RNNoise.swift`** (C bridging wrapper)

### 2.3 Windows Implementation

**Modify: `main.cpp`**
- Add `--noise-suppression` / `--no-noise-suppression` argument parsing
- Pass flag to MicrophoneCapturer

**Modify: `MicrophoneCapturer.h/.cpp`**
```cpp
class MicrophoneCapturer {
private:
    DenoiseState* m_rnnoiseLeft = nullptr;
    DenoiseState* m_rnnoiseRight = nullptr;
    bool m_noiseSuppressionEnabled = true;

    void ProcessWithRNNoise(int16_t* samples, size_t frameCount);
};
```

### 2.4 Linux Implementation

**Modify: `main.cpp`**
- Add argument parsing (same as Windows)

**Modify: `PulseMicrophoneCapturer.h/.cpp`**
- Same pattern as Windows

---

## Part 3: Settings Changes

### 3.1 UserSettings Model

**Modify: `Services/SettingsStore.cs`**

Add to `UserSettings` class:
```csharp
/// <summary>
/// Enable AI-powered noise suppression for microphone input.
/// Reduces background noise like fans, keyboards, and ambient sounds.
/// </summary>
public bool NoiseSuppression { get; set; } = true;
```

### 3.2 ViewModel Changes

**Modify: `ViewModels/AudioSettingsViewModel.cs`**

Add property:
```csharp
private bool _noiseSuppression;

public bool NoiseSuppression
{
    get => _noiseSuppression;
    set
    {
        if (_noiseSuppression == value) return;
        this.RaiseAndSetIfChanged(ref _noiseSuppression, value);
        _settingsStore.Settings.NoiseSuppression = value;
        _settingsStore.Save();
    }
}
```

Initialize in constructor:
```csharp
_noiseSuppression = _settingsStore.Settings.NoiseSuppression;
```

### 3.3 View Changes

**Modify: `Views/AudioSettingsView.axaml`**

Add toggle in the Input section (after noise gate or before it):
```xml
<!-- Noise Suppression -->
<Border Classes="SettingsCard" Margin="0,0,0,12">
    <Grid ColumnDefinitions="*,Auto">
        <StackPanel Grid.Column="0" Spacing="4">
            <TextBlock Text="Noise Suppression"
                       Classes="SettingsLabel"/>
            <TextBlock Text="AI-powered reduction of background noise (fans, keyboard, ambient sounds)"
                       Classes="SettingsDescription"
                       TextWrapping="Wrap"/>
        </StackPanel>
        <ToggleSwitch Grid.Column="1"
                      IsChecked="{Binding NoiseSuppression}"
                      VerticalAlignment="Center"/>
    </Grid>
</Border>
```

---

## Part 4: .NET Client Integration

### 4.1 NativeCaptureLocator Changes

**Modify: `Services/WebRtc/NativeCaptureLocator.cs`**

Update `GetNativeMicrophoneCaptureArgs`:
```csharp
public string GetNativeMicrophoneCaptureArgs(string microphoneId, bool noiseSuppression = true)
{
    var args = new List<string>();
    args.Add($"--microphone \"{microphoneId}\"");

    if (!noiseSuppression)
    {
        args.Add("--no-noise-suppression");
    }
    // noise suppression is enabled by default, no need to explicitly pass

    return string.Join(" ", args);
}
```

### 4.2 NativeMicrophoneManager Changes

**Modify: `Services/WebRtc/NativeMicrophoneManager.cs`**

Update `StartAsync` to accept noise suppression setting:
```csharp
public async Task<bool> StartAsync(string microphoneId, bool noiseSuppression = true)
{
    // ...
    var args = _locator.GetNativeMicrophoneCaptureArgs(microphoneId, noiseSuppression);
    // ...
}
```

### 4.3 AudioInputManager Changes

**Modify: `Services/WebRtc/AudioInputManager.cs`**

Pass noise suppression setting when starting native capture:
```csharp
var noiseSuppression = _settingsStore?.Settings.NoiseSuppression ?? true;
if (await _nativeMicManager.StartAsync(micId, noiseSuppression))
{
    // ...
}
```

**Note:** For SDL2 fallback, noise suppression won't be available (would require loading RNNoise as a native library in .NET, which is more complex). Log a message indicating this.

---

## Part 5: RNNoise Processing Details

### 5.1 Audio Processing Pipeline

```
Microphone Input (48kHz stereo)
    ↓
Split to mono channels (left, right)
    ↓
Buffer 480 samples (10ms frame)
    ↓
Convert Int16 → Float32 (RNNoise expects values in -32768 to 32767 range as float)
    ↓
rnnoise_process_frame() for each channel
    ↓
Convert Float32 → Int16
    ↓
Interleave back to stereo
    ↓
Output MCAP packet
```

### 5.2 RNNoise API

```c
// Create state (one per mono channel)
DenoiseState *rnnoise_create(RNNModel *model);  // model can be NULL for built-in

// Process one 10ms frame (480 samples at 48kHz)
// Input/output are float arrays, values in -32768 to 32767 range
float rnnoise_process_frame(DenoiseState *st, float *out, const float *in);

// Returns VAD probability (0.0 to 1.0) - could use for speaking detection!

// Cleanup
void rnnoise_destroy(DenoiseState *st);
```

### 5.3 Frame Buffering

RNNoise requires exactly 480 samples per call. Audio packets may not align:
- Buffer incoming audio
- Process complete 480-sample frames
- Keep remainder for next packet

---

## Part 6: File Summary

### New Files
- `src/SnackaCaptureVideoToolbox/Sources/SnackaCaptureVideoToolbox/RNNoise/` (C sources + bridging)
- `src/SnackaCaptureWindows/src/rnnoise/` (C sources)
- `src/SnackaCaptureLinux/src/rnnoise/` (C sources)

### Modified Files
- `src/SnackaCaptureVideoToolbox/Package.swift`
- `src/SnackaCaptureVideoToolbox/Sources/.../SnackaCaptureApp.swift`
- `src/SnackaCaptureVideoToolbox/Sources/.../MicrophoneCapturer.swift`
- `src/SnackaCaptureWindows/CMakeLists.txt`
- `src/SnackaCaptureWindows/src/main.cpp`
- `src/SnackaCaptureWindows/src/MicrophoneCapturer.h`
- `src/SnackaCaptureWindows/src/MicrophoneCapturer.cpp`
- `src/SnackaCaptureLinux/CMakeLists.txt`
- `src/SnackaCaptureLinux/src/main.cpp`
- `src/SnackaCaptureLinux/src/PulseMicrophoneCapturer.h`
- `src/SnackaCaptureLinux/src/PulseMicrophoneCapturer.cpp`
- `src/Snacka.Client/Services/SettingsStore.cs`
- `src/Snacka.Client/ViewModels/AudioSettingsViewModel.cs`
- `src/Snacka.Client/Views/AudioSettingsView.axaml`
- `src/Snacka.Client/Services/WebRtc/NativeCaptureLocator.cs`
- `src/Snacka.Client/Services/WebRtc/NativeMicrophoneManager.cs`
- `src/Snacka.Client/Services/WebRtc/AudioInputManager.cs`

---

## Part 7: Implementation Order

1. **Download and integrate RNNoise source** into all three native projects
2. **Implement on one platform first** (suggest macOS since Swift has good C interop)
3. **Add CLI flag parsing** to all platforms
4. **Implement audio processing** with RNNoise in MicrophoneCapturer
5. **Add Settings property** to UserSettings
6. **Add ViewModel property** and View toggle
7. **Update NativeCaptureLocator** to pass the flag
8. **Update AudioInputManager** to read setting and pass to native manager
9. **Test on all platforms**

---

## Part 8: Testing

### Manual Testing
1. Enable noise suppression, speak near a fan → fan noise should be reduced
2. Disable noise suppression → fan noise audible
3. Toggle mid-call → requires reconnecting mic (acceptable)
4. Verify setting persists across app restarts

### Performance Testing
- RNNoise typically uses <5% CPU on modern hardware
- Measure latency impact (should be <1ms per 10ms frame)

---

## Bonus: VAD from RNNoise

`rnnoise_process_frame()` returns a voice activity probability (0.0-1.0). We could potentially use this instead of or in addition to our RMS-based VAD for more accurate speaking detection. Consider for future enhancement.
