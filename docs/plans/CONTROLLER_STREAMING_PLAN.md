# Controller Streaming Plan

Enable remote controller input for co-op gaming during screen share sessions.

## Use Case

1. Alice shares her screen (a game) - she's playing with a controller
2. Bob watches the stream and wants to join as Player 2
3. Bob's physical controller input is tunneled to Alice's computer
4. Alice's computer creates a virtual controller that the game sees as Player 2

## Supported Platforms

| Platform | Host (Virtual Controller) | Guest (Input Capture) |
|----------|---------------------------|------------------------|
| Windows  | ViGEmBus ✅ (excellent)    | HIDSharp ✅            |
| Linux    | uinput ✅                  | HIDSharp ✅            |
| Android  | uhid ⚠️ (needs root/ADB)   | Native API ✅          |
| macOS    | DriverKit ⚠️ (complex)     | HIDSharp ✅            |

---

## Architecture

### High-Level Flow

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         GUEST (Bob's Device)                            │
│  ┌──────────────┐    ┌──────────────────┐    ┌────────────────────┐     │
│  │   Physical   │───▶│  Input Capture   │───▶│  WebRTC            │     │
│  │  Controller  │    │  (HIDSharp)      │    │  DataChannel       │     │
│  └──────────────┘    └──────────────────┘    └─────────┬──────────┘     │
└─────────────────────────────────────────────────────────┼───────────────┘
                                                          │
                                                          ▼ Network
┌─────────────────────────────────────────────────────────┼───────────────┐
│                         HOST (Alice's Device)           │               │
│  ┌────────────────────┐    ┌──────────────────┐    ┌────┴───────────┐   │
│  │   Virtual          │◀───│  Platform Driver │◀───│  WebRTC        │   │
│  │   Controller       │    │  (ViGEm/uinput/  │    │  DataChannel   │   │
│  │                    │    │   uhid/DriverKit)│    │                │   │
│  └──────────────────┬─┘    └──────────────────┘    └────────────────┘   │
│                     │                                                    │
│                     ▼                                                    │
│              ┌──────────────┐                                           │
│              │    GAME      │                                           │
│              │  (sees P2)   │                                           │
│              └──────────────┘                                           │
└─────────────────────────────────────────────────────────────────────────┘
```

### Implementation Approach

Unlike SnackaCapture (which uses separate platform-specific processes with stdout), controller streaming uses a **mixed approach**:

#### Input Capture (Guest Side) - In-Process

```
┌─────────────────────────────────────────┐
│           Avalonia App                  │
│  ┌─────────────────────────────────┐    │
│  │  HIDSharp / HIDDevices          │    │
│  │  (cross-platform NuGet package) │    │
│  └──────────────┬──────────────────┘    │
│                 ▼                       │
│  ┌─────────────────────────────────┐    │
│  │  IControllerInputService        │    │
│  │  (platform abstraction)         │    │
│  └──────────────┬──────────────────┘    │
│                 ▼                       │
│  ┌─────────────────────────────────┐    │
│  │  WebRTC DataChannel             │    │
│  └─────────────────────────────────┘    │
└─────────────────────────────────────────┘
```

**Rationale:**
- HIDSharp NuGet package already works on Windows, Linux, and macOS
- Lower latency than process spawning (critical for input)
- Simpler architecture - no IPC needed
- Android uses platform-specific code in the Android project

#### Virtual Controller (Host Side) - Mixed

| Platform | Approach | Reason |
|----------|----------|--------|
| Windows  | In-process (ViGEm.NET) | NuGet package available |
| Linux    | In-process (P/Invoke)  | uinput is simple syscalls |
| macOS    | External driver        | DriverKit must be installed separately |
| Android  | External process       | Requires shell/root access |

---

## Platform Details

### Windows (Best Support) ✅

**Host - Virtual Controller Creation:**
- Uses [ViGEmBus](https://github.com/nefarius/ViGEmBus) driver
- Signed driver, no security workarounds needed
- Supports Xbox 360, Xbox One, and DualShock 4 emulation
- .NET wrapper: [Nefarius.ViGEm.Client](https://www.nuget.org/packages/Nefarius.ViGEm.Client)
- User installs ViGEmBus driver once, then it just works

**Implementation:**
```csharp
// Using Nefarius.ViGEm.Client NuGet package
using var client = new ViGEmClient();
var controller = client.CreateXbox360Controller();
controller.Connect();

// Update state
controller.SetButtonState(Xbox360Button.A, pressed);
controller.SetAxisValue(Xbox360Axis.LeftThumbX, value);
controller.SetSliderValue(Xbox360Slider.LeftTrigger, value);
```

**Guest - Input Capture:**
- HIDSharp works on Windows
- Can also use XInput for Xbox controllers specifically
- [SharpDX.XInput](https://www.nuget.org/packages/SharpDX.XInput/) (deprecated but functional)

**References:**
- [ViGEmBus](https://github.com/nefarius/ViGEmBus)
- [ViGEm.NET](https://github.com/nefarius/ViGEm.NET)
- [Nefarius.ViGEm.Client NuGet](https://www.nuget.org/packages/Nefarius.ViGEm.Client)

---

### Linux (Great Support) ✅

**Host - Virtual Controller Creation:**
- Uses `/dev/uinput` kernel interface
- No special permissions needed (just add user to `input` group)
- This is what [Sunshine](https://github.com/LizardByte/Sunshine) uses

**Implementation:**
```csharp
// P/Invoke wrapper for uinput
// 1. Open /dev/uinput
// 2. Configure device with ioctl (UI_SET_EVBIT, UI_SET_KEYBIT, etc.)
// 3. Create device with UI_DEV_CREATE
// 4. Write input_event structs to inject input
// 5. Cleanup with UI_DEV_DESTROY
```

**Guest - Input Capture:**
- [HIDSharp](https://www.nuget.org/packages/HidSharp) works on Linux
- Can also use evdev directly via P/Invoke

**References:**
- [Sunshine Input System](https://deepwiki.com/LizardByte/Sunshine/7-input-system)
- [MoltenGamepad](https://github.com/jgeumlek/MoltenGamepad)
- [ControllerEmulator](https://github.com/WebFreak001/ControllerEmulator)

---

### Android (Limited Host Support) ⚠️

**Host - Virtual Controller Creation:**

Android is tricky because virtual input device creation requires elevated permissions.

**Option 1: uhid (Recommended for compatibility)**
- Available on all Android devices with Bluetooth support
- Requires ADB shell or root access
- Uses `hidcommand_jni` library built into Android 6.0.1+
- [AndroidUHidPureJava](https://github.com/WuDi-ZhanShen/AndroidUHidPureJava)

**Option 2: uinput**
- Only available on Android 12+ via `uinputcommand_jni`
- Some devices don't have `/dev/uinput`
- Requires ADB shell or root

**Option 3: Android 17+ Virtual Gamepad API (Future)**
- Google is adding native virtual gamepad support in Android 17
- Will be the best solution when available

**Guest - Input Capture:**
- Use Android's native [Game Controller API](https://developer.android.com/develop/ui/views/touch-and-input/game-controllers)
- Platform-specific code in Android project (not HIDSharp)
- Handle `KeyEvent` and `MotionEvent` for buttons/axes

**Practical Limitation:**
For Android as HOST, the user would need to:
1. Enable ADB debugging, or
2. Have a rooted device, or
3. Wait for Android 17+

As GUEST, Android works fine - just capture controller input and send over network.

**References:**
- [scrcpy Game Controller PR](https://github.com/Genymobile/scrcpy/pull/2130)
- [Android Game Controller Guide](https://developer.android.com/develop/ui/views/touch-and-input/game-controllers/controller-input)

---

### macOS (Complex Host Support) ⚠️

**Host - Virtual Controller Creation:**

macOS has the strictest requirements due to security restrictions.

**Option 1: DriverKit (Recommended but complex)**
- Modern replacement for kernel extensions
- Requires Apple Developer Program membership ($99/year)
- Requires special entitlement from Apple
- Must be notarized and signed
- [Karabiner-DriverKit-VirtualHIDDevice](https://github.com/pqrs-org/Karabiner-DriverKit-VirtualHIDDevice)

**Option 2: foohid (Legacy, deprecated)**
- Old IOKit driver, has security issues
- Requires disabling SIP (System Integrity Protection)
- Not recommended for production
- [foohid](https://github.com/unbit/foohid)

**Option 3: LizardByte VirtualHID**
- Research project for Sunshine macOS support
- [VirtualHID-macOS](https://github.com/LizardByte-research/VirtualHID-macOS)

**Option 4: Keyboard/Mouse Emulation Fallback**
- Use Accessibility API to simulate keyboard presses
- Loses analog stick precision
- Works for some games

**Guest - Input Capture:**
- [HIDSharp](https://www.nuget.org/packages/HidSharp) works on macOS
- Can also use IOKit directly
- [gamepad-osx](https://github.com/suzukiplan/gamepad-osx)

**Practical Limitation:**
For macOS as HOST, options are:
1. Ship a signed DriverKit extension (requires Apple approval), or
2. Ask users to install a third-party driver, or
3. Fallback to keyboard emulation

**References:**
- [Sunshine macOS Gamepad PR](https://github.com/LizardByte/Sunshine/pull/756)
- [HIDDriverKit Documentation](https://developer.apple.com/documentation/hiddriverkit)

---

## Data Protocol

### Controller State Message

```csharp
public record ControllerStateMessage
{
    // Message type identifier
    public const byte MessageType = 0x10;

    // Controller slot (0-3 for players 1-4)
    public byte ControllerIndex { get; init; }

    // Controller type
    public ControllerType Type { get; init; } // Xbox, PlayStation, Generic

    // Buttons (bitfield)
    public uint Buttons { get; init; }

    // Analog sticks (-32768 to 32767)
    public short LeftStickX { get; init; }
    public short LeftStickY { get; init; }
    public short RightStickX { get; init; }
    public short RightStickY { get; init; }

    // Triggers (0 to 255)
    public byte LeftTrigger { get; init; }
    public byte RightTrigger { get; init; }

    // Timestamp for ordering/latency measurement
    public long Timestamp { get; init; }
}

public enum ControllerType : byte
{
    Generic = 0,
    Xbox360 = 1,
    XboxOne = 2,
    DualShock4 = 3,
    DualSense = 4
}

[Flags]
public enum ControllerButtons : uint
{
    None = 0,
    A = 1 << 0,           // Cross on PlayStation
    B = 1 << 1,           // Circle
    X = 1 << 2,           // Square
    Y = 1 << 3,           // Triangle
    LeftBumper = 1 << 4,  // L1
    RightBumper = 1 << 5, // R1
    Back = 1 << 6,        // Select/Share
    Start = 1 << 7,       // Options
    LeftStick = 1 << 8,   // L3
    RightStick = 1 << 9,  // R3
    DPadUp = 1 << 10,
    DPadDown = 1 << 11,
    DPadLeft = 1 << 12,
    DPadRight = 1 << 13,
    Guide = 1 << 14,      // Xbox/PS button
}
```

### Transmission

Send over WebRTC DataChannel for lowest latency:
- Unreliable, unordered delivery (like UDP)
- Already have WebRTC connection for audio/video
- ~16-32 bytes per update, send at 60-120 Hz

---

## Implementation Phases

### Phase 1: Windows + Linux MVP (PARTIAL - Input Capture Done)

#### Input Capture (Guest Side) ✅ COMPLETE
- [x] Create `IControllerService` interface
- [x] Implement `ControllerService` using HIDSharp (Windows/Linux/macOS)
- [x] `ControllerDevice` record with Id, Name, Manufacturer, VendorId, ProductId
- [x] `ControllerState` class with 8 axes, 32 buttons, hat switch
- [x] Controller enumeration (GamePad, Joystick, MultiAxisController usage pages)
- [x] HID report parsing for axes and buttons
- [x] Settings UI: `ControllerSettingsView.axaml` with full test visualization
  - [x] Controller dropdown selection with refresh
  - [x] Left/right stick visualization (circular with crosshairs)
  - [x] Trigger bars (Rx/Ry)
  - [x] 16 button indicators with press highlighting
  - [x] D-pad/hat switch visualization
- [x] Custom converters: `AxisToPositionConverter`, `BoolToButtonColorConverter`, `HatToDPadConverter`

#### Network Transport ✅ COMPLETE
- [x] Define `ControllerStateMessage` protocol (`Snacka.Shared/Models/ControllerStreaming.cs`)
- [x] Add SignalR hub methods for controller streaming (`SnackaHub.cs`)
- [x] Client services: `ControllerStreamingService` (guest), `ControllerHostService` (host)
- [x] Access control: Request/Accept/Decline/Stop controller access flow
- [x] Controller state logged to host stdout (for debugging/Phase 1)
- [x] UI: "Share Controller" button on screen share tiles
- [x] UI: Accept/reject controller share request notification on host

#### Virtual Controller (Host Side) ✅ COMPLETE
- [x] Create `IVirtualControllerService` interface
- [x] Implement `WindowsVirtualControllerService` using ViGEm.NET (Nefarius.ViGEm.Client)
- [x] Implement `LinuxVirtualControllerService` using uinput P/Invoke
- [x] Feed received `ControllerStateMessage` to virtual controller
- [x] Factory pattern for platform-specific instantiation

### Phase 2: Android Guest Support
- [ ] Implement `AndroidControllerInputService` using Game Controller API
- [ ] Platform-specific code in Snacka.Client.Android project
- [ ] Test controller capture on Android as guest

### Phase 3: Android Host Support (Optional)
- [ ] Research ADB-less solutions
- [ ] Implement `AndroidVirtualControllerService` using uhid
- [ ] Document root/ADB requirements
- [ ] Consider waiting for Android 17 native API

### Phase 4: macOS Host Support
- [ ] Evaluate DriverKit feasibility and Apple approval process
- [ ] Implement `MacOSVirtualControllerService`
- [ ] Or implement keyboard emulation fallback
- [ ] Document any driver installation requirements

### Phase 5: Polish
- [x] Support multiple remote controllers (up to 4 players) - Already works via slot assignment
- [x] **Vibration/rumble feedback (Windows host only)**: Games on Windows host can send rumble to guest's physical controller.
      - ViGEm captures `FeedbackReceived` events from games
      - Rumble relayed via SignalR to guest
      - Guest's controller receives HID output report (DS4/Xbox One supported)
      - User can enable/disable in Controller Settings with info about Windows host requirement
- [ ] Latency compensation and interpolation
- [x] Controller disconnect/reconnect handling - Session remains open, resumes on reconnect
- [ ] Controller type auto-detection and mapping
- [x] **Mute/pause controller inputs**: Host can mute/unmute guest input via session panel button.
      When muted, guest's input is not fed to virtual controller. Session remains active.

---

## Service Interfaces

```csharp
// Input capture (guest side)
public interface IControllerInputService
{
    /// <summary>
    /// Gets available controllers connected to this device.
    /// </summary>
    IReadOnlyList<ControllerInfo> GetConnectedControllers();

    /// <summary>
    /// Starts capturing input from a controller.
    /// </summary>
    void StartCapture(int controllerIndex);

    /// <summary>
    /// Stops capturing input.
    /// </summary>
    void StopCapture(int controllerIndex);

    /// <summary>
    /// Fired when controller state changes.
    /// </summary>
    event Action<ControllerStateMessage>? StateChanged;

    /// <summary>
    /// Fired when a controller is connected/disconnected.
    /// </summary>
    event Action<ControllerInfo, bool>? ControllerConnectionChanged;
}

// Virtual controller (host side)
public interface IVirtualControllerService
{
    /// <summary>
    /// Creates a virtual controller.
    /// </summary>
    int CreateController(ControllerType type);

    /// <summary>
    /// Destroys a virtual controller.
    /// </summary>
    void DestroyController(int controllerId);

    /// <summary>
    /// Updates the state of a virtual controller.
    /// </summary>
    void UpdateState(int controllerId, ControllerStateMessage state);

    /// <summary>
    /// Sends rumble/vibration to the virtual controller.
    /// </summary>
    void SetVibration(int controllerId, byte leftMotor, byte rightMotor);

    /// <summary>
    /// Returns true if virtual controller creation is supported on this platform.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Returns a message explaining why virtual controllers aren't supported (if !IsSupported).
    /// </summary>
    string? UnsupportedReason { get; }
}

public record ControllerInfo(
    int Index,
    string Name,
    ControllerType Type,
    Guid DeviceId
);
```

---

## Technical Challenges

| Challenge | Mitigation |
|-----------|------------|
| Windows requires ViGEmBus driver | Prompt user to install, provide download link |
| macOS DriverKit signing | May need keyboard fallback or third-party driver |
| Android requires root/ADB for host | Document limitation, wait for Android 17 |
| Input latency | Use WebRTC DataChannel, optimize update frequency |
| Controller type differences | Normalize to common format, provide mappings |
| Avalonia has no gamepad API | Use HIDSharp + platform-specific code |
| Anti-cheat may block virtual controllers | Document limitation, works for most games |

---

## Recommended NuGet Packages

```xml
<!-- Cross-platform HID access for input capture -->
<PackageReference Include="HidSharp" Version="2.6.4" />

<!-- Higher-level gamepad abstraction (optional) -->
<PackageReference Include="HIDDevices" Version="4.1.2" />

<!-- Windows virtual controller (ViGEmBus client) -->
<PackageReference Include="Nefarius.ViGEm.Client" Version="1.22.0" Condition="$(RuntimeIdentifier.StartsWith('win'))" />
```

---

## Sources

### General
- [Sunshine (LizardByte)](https://github.com/LizardByte/Sunshine) - Reference implementation
- [Sunshine Input System](https://deepwiki.com/LizardByte/Sunshine/7-input-system)
- [Sunshine Gamepad Support](https://deepwiki.com/LizardByte/Sunshine/7.1-gamepad-support)

### Windows
- [ViGEmBus](https://github.com/nefarius/ViGEmBus)
- [ViGEm.NET](https://github.com/nefarius/ViGEm.NET)
- [Nefarius.ViGEm.Client](https://www.nuget.org/packages/Nefarius.ViGEm.Client)

### Linux
- [MoltenGamepad](https://github.com/jgeumlek/MoltenGamepad)
- [ControllerEmulator](https://github.com/WebFreak001/ControllerEmulator)

### Android
- [scrcpy Game Controller PR](https://github.com/Genymobile/scrcpy/pull/2130)
- [AndroidUHidPureJava](https://github.com/WuDi-ZhanShen/AndroidUHidPureJava)
- [Android Game Controller Guide](https://developer.android.com/develop/ui/views/touch-and-input/game-controllers)

### macOS
- [Sunshine macOS Gamepad PR](https://github.com/LizardByte/Sunshine/pull/756)
- [VirtualHID-macOS](https://github.com/LizardByte-research/VirtualHID-macOS)
- [Karabiner-DriverKit-VirtualHIDDevice](https://github.com/pqrs-org/Karabiner-DriverKit-VirtualHIDDevice)
- [foohid](https://github.com/unbit/foohid) (legacy)
- [HIDDriverKit Documentation](https://developer.apple.com/documentation/hiddriverkit)

### Input Capture
- [HIDSharp](https://www.nuget.org/packages/HidSharp)
- [HIDDevices](https://github.com/DevDecoder/HIDDevices)
- [gamepad-osx](https://github.com/suzukiplan/gamepad-osx)

---

**Last Updated:** 2026-01-16
**Status:** ~95% - Phase 1 complete + Phase 5 polish items! Features:
- Input capture (HIDSharp), network transport (SignalR), virtual controller (ViGEm/uinput) all working
- Controller session panel with mute/unmute and disconnect controls
- Controller requirements info in settings panel
- Controller disconnect/reconnect handling (session remains active)
- Multiple controller slots (up to 4 players) supported
- **Vibration/rumble feedback** (Windows host only) - relays game rumble to guest's physical controller

Remaining: Latency compensation, controller type detection. Android/macOS host support deferred.
