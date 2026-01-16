#if LINUX
using System.Runtime.InteropServices;
using Snacka.Shared.Models;

namespace Snacka.Client.Services;

/// <summary>
/// Virtual controller service using uinput on Linux.
/// Creates Xbox 360-like controller emulation that games recognize.
/// Requires user to be in the 'input' group: sudo usermod -a -G input $USER
/// </summary>
public class LinuxVirtualControllerService : IVirtualControllerService
{
    // uinput constants
    private const string UInputPath = "/dev/uinput";
    private const int O_WRONLY = 0x0001;
    private const int O_NONBLOCK = 0x0800;

    // ioctl request codes
    private const uint UI_SET_EVBIT = 0x40045564;   // _IOW('U', 100, int)
    private const uint UI_SET_KEYBIT = 0x40045565;  // _IOW('U', 101, int)
    private const uint UI_SET_ABSBIT = 0x40045567;  // _IOW('U', 103, int)
    private const uint UI_DEV_CREATE = 0x5501;
    private const uint UI_DEV_DESTROY = 0x5502;
    private const uint UI_DEV_SETUP = 0x405c5503;   // _IOW('U', 3, struct uinput_setup)

    // Event types
    private const int EV_SYN = 0x00;
    private const int EV_KEY = 0x01;
    private const int EV_ABS = 0x03;

    // Sync codes
    private const int SYN_REPORT = 0;

    // Button codes (Xbox 360 mapping)
    private const int BTN_A = 0x130;
    private const int BTN_B = 0x131;
    private const int BTN_X = 0x133;
    private const int BTN_Y = 0x134;
    private const int BTN_TL = 0x136;  // Left bumper
    private const int BTN_TR = 0x137;  // Right bumper
    private const int BTN_SELECT = 0x13a;
    private const int BTN_START = 0x13b;
    private const int BTN_MODE = 0x13c;  // Guide
    private const int BTN_THUMBL = 0x13d;
    private const int BTN_THUMBR = 0x13e;
    private const int BTN_DPAD_UP = 0x220;
    private const int BTN_DPAD_DOWN = 0x221;
    private const int BTN_DPAD_LEFT = 0x222;
    private const int BTN_DPAD_RIGHT = 0x223;

    // Axis codes
    private const int ABS_X = 0x00;   // Left stick X
    private const int ABS_Y = 0x01;   // Left stick Y
    private const int ABS_RX = 0x03;  // Right stick X
    private const int ABS_RY = 0x04;  // Right stick Y
    private const int ABS_Z = 0x02;   // Left trigger
    private const int ABS_RZ = 0x05;  // Right trigger

    // P/Invoke declarations
    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, int value);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, ref UInputSetup setup);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, ref UInputAbsSetup absSetup);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, ref InputEvent ev, nint count);

    // Structures
    [StructLayout(LayoutKind.Sequential)]
    private struct InputEvent
    {
        public long Seconds;
        public long Microseconds;
        public ushort Type;
        public ushort Code;
        public int Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UInputSetup
    {
        public InputId Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string Name;
        public uint FfEffectsMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputId
    {
        public ushort BusType;
        public ushort Vendor;
        public ushort Product;
        public ushort Version;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UInputAbsSetup
    {
        public ushort Code;
        public InputAbsInfo AbsInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputAbsInfo
    {
        public int Value;
        public int Minimum;
        public int Maximum;
        public int Fuzz;
        public int Flat;
        public int Resolution;
    }

    private const ushort BUS_USB = 0x03;
    private const ushort VENDOR_MICROSOFT = 0x045e;
    private const ushort PRODUCT_XBOX360 = 0x028e;

    private readonly int[] _fds = new int[4] { -1, -1, -1, -1 };
    private bool _isSupported;
    private string? _notSupportedReason;

    public LinuxVirtualControllerService()
    {
        // Check if uinput is available
        if (!File.Exists(UInputPath))
        {
            _isSupported = false;
            _notSupportedReason = "/dev/uinput not found. Make sure the uinput module is loaded.";
            Console.WriteLine($"LinuxVirtualControllerService: {_notSupportedReason}");
            return;
        }

        // Try to open it to check permissions
        int fd = open(UInputPath, O_WRONLY | O_NONBLOCK);
        if (fd < 0)
        {
            _isSupported = false;
            _notSupportedReason = "Cannot open /dev/uinput. Add user to 'input' group: sudo usermod -a -G input $USER";
            Console.WriteLine($"LinuxVirtualControllerService: {_notSupportedReason}");
            return;
        }
        close(fd);

        _isSupported = true;
        Console.WriteLine("LinuxVirtualControllerService: uinput available and accessible");
    }

    public bool IsSupported => _isSupported;
    public string? NotSupportedReason => _notSupportedReason;
    public int ActiveControllerCount => _fds.Count(fd => fd >= 0);

    public bool CreateController(byte slot)
    {
        if (!_isSupported || slot >= 4)
        {
            Console.WriteLine($"LinuxVirtualControllerService: Cannot create controller at slot {slot}");
            return false;
        }

        if (_fds[slot] >= 0)
        {
            Console.WriteLine($"LinuxVirtualControllerService: Controller already exists at slot {slot}");
            return true;
        }

        try
        {
            int fd = open(UInputPath, O_WRONLY | O_NONBLOCK);
            if (fd < 0)
            {
                Console.WriteLine($"LinuxVirtualControllerService: Failed to open uinput");
                return false;
            }

            // Set up event types
            if (ioctl(fd, UI_SET_EVBIT, EV_KEY) < 0 ||
                ioctl(fd, UI_SET_EVBIT, EV_ABS) < 0 ||
                ioctl(fd, UI_SET_EVBIT, EV_SYN) < 0)
            {
                Console.WriteLine("LinuxVirtualControllerService: Failed to set event bits");
                close(fd);
                return false;
            }

            // Set up buttons
            int[] buttons = [BTN_A, BTN_B, BTN_X, BTN_Y, BTN_TL, BTN_TR, BTN_SELECT, BTN_START,
                BTN_MODE, BTN_THUMBL, BTN_THUMBR, BTN_DPAD_UP, BTN_DPAD_DOWN, BTN_DPAD_LEFT, BTN_DPAD_RIGHT];

            foreach (var btn in buttons)
            {
                if (ioctl(fd, UI_SET_KEYBIT, btn) < 0)
                {
                    Console.WriteLine($"LinuxVirtualControllerService: Failed to set key bit {btn}");
                    close(fd);
                    return false;
                }
            }

            // Set up axes
            int[] axes = [ABS_X, ABS_Y, ABS_RX, ABS_RY, ABS_Z, ABS_RZ];
            foreach (var axis in axes)
            {
                if (ioctl(fd, UI_SET_ABSBIT, axis) < 0)
                {
                    Console.WriteLine($"LinuxVirtualControllerService: Failed to set abs bit {axis}");
                    close(fd);
                    return false;
                }
            }

            // Configure device
            var setup = new UInputSetup
            {
                Id = new InputId
                {
                    BusType = BUS_USB,
                    Vendor = VENDOR_MICROSOFT,
                    Product = PRODUCT_XBOX360,
                    Version = 1
                },
                Name = $"Snacka Virtual Controller {slot + 1}",
                FfEffectsMax = 0
            };

            if (ioctl(fd, UI_DEV_SETUP, ref setup) < 0)
            {
                Console.WriteLine("LinuxVirtualControllerService: Failed to setup device");
                close(fd);
                return false;
            }

            // Create the device
            if (ioctl(fd, UI_DEV_CREATE, 0) < 0)
            {
                Console.WriteLine("LinuxVirtualControllerService: Failed to create device");
                close(fd);
                return false;
            }

            _fds[slot] = fd;
            Console.WriteLine($"LinuxVirtualControllerService: Created virtual controller at slot {slot} (Player {slot + 1})");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LinuxVirtualControllerService: Exception creating controller: {ex.Message}");
            return false;
        }
    }

    public bool DestroyController(byte slot)
    {
        if (slot >= 4 || _fds[slot] < 0)
        {
            return false;
        }

        try
        {
            ioctl(_fds[slot], UI_DEV_DESTROY, 0);
            close(_fds[slot]);
            _fds[slot] = -1;
            Console.WriteLine($"LinuxVirtualControllerService: Destroyed controller at slot {slot}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LinuxVirtualControllerService: Exception destroying controller: {ex.Message}");
            return false;
        }
    }

    public void UpdateState(byte slot, ControllerStateMessage state)
    {
        if (slot >= 4 || _fds[slot] < 0)
        {
            return;
        }

        int fd = _fds[slot];
        var buttons = (ControllerButtons)state.Buttons;

        try
        {
            // Send button events
            WriteKey(fd, BTN_A, buttons.HasFlag(ControllerButtons.A));
            WriteKey(fd, BTN_B, buttons.HasFlag(ControllerButtons.B));
            WriteKey(fd, BTN_X, buttons.HasFlag(ControllerButtons.X));
            WriteKey(fd, BTN_Y, buttons.HasFlag(ControllerButtons.Y));
            WriteKey(fd, BTN_TL, buttons.HasFlag(ControllerButtons.LeftBumper));
            WriteKey(fd, BTN_TR, buttons.HasFlag(ControllerButtons.RightBumper));
            WriteKey(fd, BTN_SELECT, buttons.HasFlag(ControllerButtons.Back));
            WriteKey(fd, BTN_START, buttons.HasFlag(ControllerButtons.Start));
            WriteKey(fd, BTN_MODE, buttons.HasFlag(ControllerButtons.Guide));
            WriteKey(fd, BTN_THUMBL, buttons.HasFlag(ControllerButtons.LeftStick));
            WriteKey(fd, BTN_THUMBR, buttons.HasFlag(ControllerButtons.RightStick));
            WriteKey(fd, BTN_DPAD_UP, buttons.HasFlag(ControllerButtons.DPadUp));
            WriteKey(fd, BTN_DPAD_DOWN, buttons.HasFlag(ControllerButtons.DPadDown));
            WriteKey(fd, BTN_DPAD_LEFT, buttons.HasFlag(ControllerButtons.DPadLeft));
            WriteKey(fd, BTN_DPAD_RIGHT, buttons.HasFlag(ControllerButtons.DPadRight));

            // Send axis events (short -32768 to 32767)
            WriteAbs(fd, ABS_X, state.LeftStickX);
            WriteAbs(fd, ABS_Y, state.LeftStickY);
            WriteAbs(fd, ABS_RX, state.RightStickX);
            WriteAbs(fd, ABS_RY, state.RightStickY);

            // Triggers (byte 0-255 -> int 0-255)
            WriteAbs(fd, ABS_Z, state.LeftTrigger);
            WriteAbs(fd, ABS_RZ, state.RightTrigger);

            // Sync
            WriteSync(fd);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LinuxVirtualControllerService: Error updating state: {ex.Message}");
        }
    }

    private void WriteKey(int fd, int code, bool pressed)
    {
        var ev = new InputEvent
        {
            Type = EV_KEY,
            Code = (ushort)code,
            Value = pressed ? 1 : 0
        };
        write(fd, ref ev, Marshal.SizeOf<InputEvent>());
    }

    private void WriteAbs(int fd, int code, int value)
    {
        var ev = new InputEvent
        {
            Type = EV_ABS,
            Code = (ushort)code,
            Value = value
        };
        write(fd, ref ev, Marshal.SizeOf<InputEvent>());
    }

    private void WriteSync(int fd)
    {
        var ev = new InputEvent
        {
            Type = EV_SYN,
            Code = SYN_REPORT,
            Value = 0
        };
        write(fd, ref ev, Marshal.SizeOf<InputEvent>());
    }

    public bool HasController(byte slot)
    {
        return slot < 4 && _fds[slot] >= 0;
    }

    public void Dispose()
    {
        for (byte i = 0; i < 4; i++)
        {
            DestroyController(i);
        }
        Console.WriteLine("LinuxVirtualControllerService: Disposed");
    }
}
#endif
