using System.Management;
using System.Runtime.InteropServices;

namespace MagicKeyboard.Gui;

// Display brightness: WMI for internal (laptop) panels, DDC/CI for external monitors that support it.
// Windows has no single API covering both; monitors without DDC/CI simply don't change.
internal static class Brightness
{
    private static readonly object Gate = new();

    public static void Step(int deltaPercent)
    {
        lock (Gate)   // DDC/CI is slow; don't let repeats pile up concurrently
        {
            try { StepWmi(deltaPercent); } catch { /* no internal panel */ }
            try { StepDdc(deltaPercent); } catch { /* no DDC/CI */ }
        }
    }

    private static void StepWmi(int delta)
    {
        var scope = new ManagementScope(@"root\WMI");
        int current = -1;
        using (var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness")))
            foreach (ManagementObject o in q.Get()) { current = Convert.ToInt32(o["CurrentBrightness"]); break; }
        if (current < 0) return;

        int target = Math.Clamp(current + delta, 0, 100);
        using var m = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods"));
        foreach (ManagementObject o in m.Get())
            o.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)target });
    }

    private static void StepDdc(int delta)
    {
        var monitors = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) => { monitors.Add(h); return true; }, IntPtr.Zero);
        foreach (var hmon in monitors)
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out uint n) || n == 0) continue;
            var phys = new PHYSICAL_MONITOR[n];
            if (!GetPhysicalMonitorsFromHMONITOR(hmon, n, phys)) continue;
            try
            {
                foreach (var p in phys)
                {
                    if (!GetMonitorBrightness(p.hPhysicalMonitor, out uint min, out uint cur, out uint max) || max <= min) continue;
                    int range = (int)(max - min);
                    int target = Math.Clamp((int)cur + delta * range / 100, (int)min, (int)max);
                    SetMonitorBrightness(p.hPhysicalMonitor, (uint)target);
                }
            }
            finally { DestroyPhysicalMonitors(n, phys); }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr monitor, uint brightness);
}
