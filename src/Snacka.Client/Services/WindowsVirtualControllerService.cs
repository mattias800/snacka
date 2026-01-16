#if WINDOWS
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Snacka.Shared.Models;

namespace Snacka.Client.Services;

/// <summary>
/// Virtual controller service using ViGEm on Windows.
/// Creates Xbox 360 controller emulation that games recognize as real controllers.
/// Requires ViGEmBus driver to be installed: https://github.com/nefarius/ViGEmBus
/// </summary>
public class WindowsVirtualControllerService : IVirtualControllerService
{
    private ViGEmClient? _client;
    private readonly IXbox360Controller?[] _controllers = new IXbox360Controller?[4];
    private bool _isSupported;
    private string? _notSupportedReason;

    public WindowsVirtualControllerService()
    {
        try
        {
            _client = new ViGEmClient();
            _isSupported = true;
            Console.WriteLine("WindowsVirtualControllerService: ViGEm client initialized successfully");
        }
        catch (Exception ex)
        {
            _isSupported = false;
            _notSupportedReason = $"ViGEmBus driver not installed or not working: {ex.Message}. " +
                "Download from: https://github.com/nefarius/ViGEmBus/releases";
            Console.WriteLine($"WindowsVirtualControllerService: {_notSupportedReason}");
        }
    }

    public bool IsSupported => _isSupported;
    public string? NotSupportedReason => _notSupportedReason;
    public int ActiveControllerCount => _controllers.Count(c => c != null);

    public bool CreateController(byte slot)
    {
        if (!_isSupported || _client == null || slot >= 4)
        {
            Console.WriteLine($"WindowsVirtualControllerService: Cannot create controller at slot {slot}");
            return false;
        }

        if (_controllers[slot] != null)
        {
            Console.WriteLine($"WindowsVirtualControllerService: Controller already exists at slot {slot}");
            return true; // Already exists
        }

        try
        {
            var controller = _client.CreateXbox360Controller();
            controller.Connect();
            _controllers[slot] = controller;
            Console.WriteLine($"WindowsVirtualControllerService: Created Xbox 360 controller at slot {slot} (Player {slot + 1})");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WindowsVirtualControllerService: Failed to create controller at slot {slot}: {ex.Message}");
            return false;
        }
    }

    public bool DestroyController(byte slot)
    {
        if (slot >= 4 || _controllers[slot] == null)
        {
            return false;
        }

        try
        {
            _controllers[slot]!.Disconnect();
            _controllers[slot] = null;
            Console.WriteLine($"WindowsVirtualControllerService: Destroyed controller at slot {slot}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WindowsVirtualControllerService: Failed to destroy controller at slot {slot}: {ex.Message}");
            return false;
        }
    }

    public void UpdateState(byte slot, ControllerStateMessage state)
    {
        if (slot >= 4 || _controllers[slot] == null)
        {
            return;
        }

        var controller = _controllers[slot]!;

        try
        {
            // Convert ControllerStateMessage to Xbox 360 controller state
            var buttons = (ControllerButtons)state.Buttons;

            // Set buttons
            controller.SetButtonState(Xbox360Button.A, buttons.HasFlag(ControllerButtons.A));
            controller.SetButtonState(Xbox360Button.B, buttons.HasFlag(ControllerButtons.B));
            controller.SetButtonState(Xbox360Button.X, buttons.HasFlag(ControllerButtons.X));
            controller.SetButtonState(Xbox360Button.Y, buttons.HasFlag(ControllerButtons.Y));
            controller.SetButtonState(Xbox360Button.LeftShoulder, buttons.HasFlag(ControllerButtons.LeftBumper));
            controller.SetButtonState(Xbox360Button.RightShoulder, buttons.HasFlag(ControllerButtons.RightBumper));
            controller.SetButtonState(Xbox360Button.Back, buttons.HasFlag(ControllerButtons.Back));
            controller.SetButtonState(Xbox360Button.Start, buttons.HasFlag(ControllerButtons.Start));
            controller.SetButtonState(Xbox360Button.LeftThumb, buttons.HasFlag(ControllerButtons.LeftStick));
            controller.SetButtonState(Xbox360Button.RightThumb, buttons.HasFlag(ControllerButtons.RightStick));
            controller.SetButtonState(Xbox360Button.Guide, buttons.HasFlag(ControllerButtons.Guide));

            // Set D-pad
            controller.SetButtonState(Xbox360Button.Up, buttons.HasFlag(ControllerButtons.DPadUp));
            controller.SetButtonState(Xbox360Button.Down, buttons.HasFlag(ControllerButtons.DPadDown));
            controller.SetButtonState(Xbox360Button.Left, buttons.HasFlag(ControllerButtons.DPadLeft));
            controller.SetButtonState(Xbox360Button.Right, buttons.HasFlag(ControllerButtons.DPadRight));

            // Set analog sticks (short values -32768 to 32767)
            controller.SetAxisValue(Xbox360Axis.LeftThumbX, state.LeftStickX);
            controller.SetAxisValue(Xbox360Axis.LeftThumbY, state.LeftStickY);
            controller.SetAxisValue(Xbox360Axis.RightThumbX, state.RightStickX);
            controller.SetAxisValue(Xbox360Axis.RightThumbY, state.RightStickY);

            // Set triggers (byte values 0-255)
            controller.SetSliderValue(Xbox360Slider.LeftTrigger, state.LeftTrigger);
            controller.SetSliderValue(Xbox360Slider.RightTrigger, state.RightTrigger);

            // Submit the report
            controller.SubmitReport();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WindowsVirtualControllerService: Failed to update state for slot {slot}: {ex.Message}");
        }
    }

    public bool HasController(byte slot)
    {
        return slot < 4 && _controllers[slot] != null;
    }

    public void Dispose()
    {
        // Disconnect all controllers
        for (byte i = 0; i < 4; i++)
        {
            DestroyController(i);
        }

        _client?.Dispose();
        _client = null;
        Console.WriteLine("WindowsVirtualControllerService: Disposed");
    }
}
#endif
