using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace MagicKeyboard.Shared.Hid;

// Drops the Bluetooth connection to a paired device so it can go into deep sleep. A Magic Keyboard
// reconnects by itself on the next key press (that key itself may be lost); it stays paired.
// Same as MagicMouse's BluetoothLink.
public static class BluetoothLink
{
    // bthioctl.h: BTH_CTL(BTH_IOCTL_BASE + 0x03) = CTL_CODE(FILE_DEVICE_BLUETOOTH, 3, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private const uint IOCTL_BTH_DISCONNECT_DEVICE = 0x0041000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_FIND_RADIO_PARAMS { public int dwSize; }

    [DllImport("bluetoothapis.dll", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS p, out SafeFileHandle radio);

    [DllImport("bluetoothapis.dll", SetLastError = true)]
    private static extern bool BluetoothFindRadioClose(IntPtr find);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref ulong inBuf, int inLen,
        IntPtr outBuf, int outLen, out int returned, IntPtr overlapped);

    // The address in a BTHENUM instance ID, e.g. "...\7&4af2e5&0&3809FB16A5AB_C00000000".
    public static ulong? AddressFromInstanceId(string? instanceId)
    {
        var m = instanceId == null ? null : Regex.Match(instanceId, @"&([0-9A-Fa-f]{12})_C[0-9A-Fa-f]+$");
        return m is { Success: true } &&
               ulong.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var a)
            ? a : null;
    }

    // Disconnects the device. Returns 0 on success, otherwise the Win32 error code.
    public static int Disconnect(ulong address)
    {
        var p = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        IntPtr find = BluetoothFindFirstRadio(ref p, out var radio);
        if (find == IntPtr.Zero) return Marshal.GetLastWin32Error();
        try
        {
            using (radio)
            {
                return DeviceIoControl(radio, IOCTL_BTH_DISCONNECT_DEVICE, ref address, sizeof(ulong),
                    IntPtr.Zero, 0, out _, IntPtr.Zero) ? 0 : Marshal.GetLastWin32Error();
            }
        }
        finally { BluetoothFindRadioClose(find); }
    }
}
