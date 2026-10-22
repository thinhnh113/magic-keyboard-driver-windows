using Microsoft.Win32.SafeHandles;

namespace MagicKeyboard.Shared.Hid;

public sealed record HidCollection(string Path, string? InstanceId, ushort VendorId, ushort ProductId,
    ushort UsagePage, ushort Usage, int InputLength, string? Product);

public sealed record BatteryStatus(string Device, int Percent, bool? Charging, string Source);

// Reads the Magic Keyboard battery the same way MagicMouse does (see ..\magic_mouse BatteryReader):
//  1. GET_REPORT(0x90) on the non-exclusive vendor collection (UsagePage 0xFF00, Usage 0x14).
//     Report 0x90 = [0x90, flags (bit1 charging), level]. Declared in the keyboard's SDP descriptor.
//  2. The battery property Windows' Bluetooth stack publishes on the BTHENUM devnode.
public static class KeyboardBattery
{
    public const byte BatteryReportId = 0x90;
    public static readonly ushort[] AppleVendorIds = { 0x05AC, 0x004C };
    public static readonly ushort[] KeyboardProductIds = { 0x0267, 0x026C, 0x029C, 0x029A, 0x029F, 0x0320, 0x0321, 0x0322 };

    public static bool IsMagicKeyboard(ushort vid, ushort pid) =>
        AppleVendorIds.Contains(vid) && KeyboardProductIds.Contains(pid);

    // One entry per keyboard (deduplicated by device), best source first.
    public static List<BatteryStatus> ReadAll()
    {
        var results = new Dictionary<string, BatteryStatus>(StringComparer.OrdinalIgnoreCase);
        var collections = Enumerate(IsMagicKeyboard).ToList();

        foreach (var c in collections.Where(c => c.UsagePage == 0xFF00 && c.Usage == 0x14 && c.InputLength >= 3))
        {
            var key = DeviceKey(c);
            if (results.ContainsKey(key)) continue;
            var s = ReadHid(c);
            if (s != null) results[key] = s;
        }
        foreach (var c in collections)
        {
            var key = DeviceKey(c);
            if (results.ContainsKey(key) || c.InstanceId == null) continue;
            var s = ReadDevProperty(c);
            if (s != null) results[key] = s;
        }
        return results.Values.ToList();
    }

    public static BatteryStatus? Decode(string device, ReadOnlySpan<byte> report)
    {
        if (report.Length < 3 || report[0] != BatteryReportId) return null;
        if (report[1] == 0 && report[2] == 0) return null;   // no real answer, not an empty battery
        int level = report[2];
        int percent = level <= 100 ? level : level * 100 / 255;
        return new BatteryStatus(device, percent, (report[1] & 0x02) != 0, "HID report 0x90");
    }

    public static IEnumerable<HidCollection> Enumerate(Func<ushort, ushort, bool>? filter = null)
    {
        HidNative.HidD_GetHidGuid(out var guid);
        foreach (var path in HidNative.GetInterfaces(guid))
        {
            using var h = Open(path, readWrite: false);
            if (h.IsInvalid) continue;
            var attr = new HidNative.HIDD_ATTRIBUTES { Size = System.Runtime.InteropServices.Marshal.SizeOf<HidNative.HIDD_ATTRIBUTES>() };
            if (!HidNative.HidD_GetAttributes(h, ref attr)) continue;
            if (filter != null && !filter(attr.VendorID, attr.ProductID)) continue;

            HidNative.HIDP_CAPS caps = default;
            if (HidNative.HidD_GetPreparsedData(h, out var pp))
            {
                HidNative.HidP_GetCaps(pp, out caps);
                HidNative.HidD_FreePreparsedData(pp);
            }
            var name = new char[128];
            string? product = HidNative.HidD_GetProductString(h, name, name.Length * 2) ? new string(name).TrimEnd('\0') : null;
            yield return new HidCollection(path, HidNative.GetInterfaceInstanceId(path), attr.VendorID, attr.ProductID,
                caps.UsagePage, caps.Usage, caps.InputReportByteLength, product);
        }
    }

    private static BatteryStatus? ReadHid(HidCollection c)
    {
        using var h = Open(c.Path, readWrite: true);
        if (h.IsInvalid) return null;
        var buf = new byte[Math.Max(c.InputLength, 3)];
        buf[0] = BatteryReportId;
        if (!HidNative.HidD_GetInputReport(h, buf, buf.Length)) return null;
        return Decode(Name(c), buf);
    }

    private static BatteryStatus? ReadDevProperty(HidCollection c)
    {
        if (HidNative.CM_Locate_DevNodeW(out uint inst, c.InstanceId!, 0) != HidNative.CR_SUCCESS) return null;
        for (int depth = 0; depth < 3; depth++)   // walk up from the HID collection to the BTHENUM devnode
        {
            var key = HidNative.DEVPKEY_Bluetooth_Battery;
            int size = 1;
            var b = new byte[1];
            if (HidNative.CM_Get_DevNode_PropertyW(inst, ref key, out _, b, ref size, 0) == HidNative.CR_SUCCESS)
                return new BatteryStatus(Name(c), b[0], null, "Windows Bluetooth");
            if (HidNative.CM_Get_Parent(out inst, inst, 0) != HidNative.CR_SUCCESS) break;
        }
        return null;
    }

    // The battery collection opened for ReadFile (input reports the keyboard sends by itself).
    public static SafeFileHandle OpenForReading(HidCollection c) =>
        HidNative.CreateFile(c.Path, HidNative.GENERIC_READ, HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
            IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);

    private static SafeFileHandle Open(string path, bool readWrite) =>
        HidNative.CreateFile(path, readWrite ? HidNative.GENERIC_READ | HidNative.GENERIC_WRITE : 0,
            HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE, IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);

    private static string Name(HidCollection c) =>
        string.IsNullOrWhiteSpace(c.Product) ? "Magic Keyboard" : c.Product!;

    // Collections of one keyboard share the instance-ID prefix up to "&COL" (BT) or the device part (USB).
    private static string DeviceKey(HidCollection c)
    {
        var id = c.InstanceId ?? c.Path;
        int col = id.IndexOf("&COL", StringComparison.OrdinalIgnoreCase);
        return col > 0 ? id[..col] : $"{c.VendorId:X4}:{c.ProductId:X4}";
    }
}
