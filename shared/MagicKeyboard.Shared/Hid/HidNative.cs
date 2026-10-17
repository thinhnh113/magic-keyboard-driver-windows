using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MagicKeyboard.Shared.Hid;

// hid.dll / cfgmgr32 interop, ported from MagicMouse.Core.Native.NativeMethods.
internal static class HidNative
{
    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    public const uint CR_SUCCESS = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    public static readonly DEVPROPKEY DEVPKEY_Device_InstanceId =
        new() { fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), pid = 256 };
    // Battery level that Windows' Bluetooth stack publishes for classic BT HID devices (Win11).
    public static readonly DEVPROPKEY DEVPKEY_Bluetooth_Battery =
        new() { fmtid = new Guid("104ea319-6ee2-4701-bd47-8ddbf425bbe5"), pid = 2 };

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec,
        uint disposition, uint flags, IntPtr template);

    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool HidD_GetProductString(SafeFileHandle h, char[] buf, int len);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_Device_Interface_List_SizeW(out int len, ref Guid cls, string? deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_Device_Interface_ListW(ref Guid cls, string? deviceId, char[] buffer, int len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_Device_Interface_PropertyW(string iface, ref DEVPROPKEY key, out uint type,
        byte[]? buffer, ref int size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type,
        byte[]? buffer, ref int size, uint flags);

    public static List<string> GetInterfaces(Guid cls)
    {
        var result = new List<string>();
        if (CM_Get_Device_Interface_List_SizeW(out int len, ref cls, null, 0) != CR_SUCCESS || len <= 1) return result;
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref cls, null, buf, len, 0) != CR_SUCCESS) return result;
        result.AddRange(new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries));
        return result;
    }

    public static string? GetInterfaceInstanceId(string iface)
    {
        var key = DEVPKEY_Device_InstanceId;
        int size = 0;
        CM_Get_Device_Interface_PropertyW(iface, ref key, out _, null, ref size, 0);
        if (size <= 0) return null;
        var buf = new byte[size];
        if (CM_Get_Device_Interface_PropertyW(iface, ref key, out _, buf, ref size, 0) != CR_SUCCESS) return null;
        return System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }
}
