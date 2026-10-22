using System.Diagnostics;
using MagicKeyboard.Shared;

// KeyProbe: diagnostics for MagicKeyboardFilter.
//   KeyProbe              driver status and filtered keyboards
//   KeyProbe raw [s]      stream raw keyboard reports for s seconds (default 15), decoded
// The driver has one event queue, so close the MagicKeyboard app before "raw" (it would steal events).

var mode = args.FirstOrDefault() ?? "info";
int seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 15;

if (mode == "battery")   // works without the driver
{
    foreach (var c in MagicKeyboard.Shared.Hid.KeyboardBattery.Enumerate(MagicKeyboard.Shared.Hid.KeyboardBattery.IsMagicKeyboard))
        Console.WriteLine($"  {c.VendorId:X4}:{c.ProductId:X4} page {c.UsagePage:X4} usage {c.Usage:X2} in {c.InputLength}  {c.Product}");
    var all = MagicKeyboard.Shared.Hid.KeyboardBattery.ReadAll();
    if (all.Count == 0) Console.WriteLine("battery: not available");
    foreach (var b in all) Console.WriteLine($"battery: {b.Device} {b.Percent}% charging={b.Charging} via {b.Source}");
    return 0;
}

using var ch = DriverChannel.TryOpen();
if (ch == null)
{
    Console.WriteLine($"MagicKeyboardFilter not present ({Protocol.UserPath} cannot be opened).");
    Console.WriteLine("Is the driver installed and the keyboard turned on? (The control device exists only while a keyboard is attached.)");
    return 1;
}

var status = ch.GetStatus();
if (mode == "raw") return Raw(ch, status, seconds, args.Contains("--force"));

Console.WriteLine($"protocol v{status.ProtocolVersion}  controlDevice=#{status.ControlGeneration}  configLoaded={status.ConfigLoaded}  appSubscribers={status.Subscribers}");
foreach (var d in status.Devices)
{
    Console.WriteLine($"#{d.Index} PID {d.ProductId:X4} {d.Transport}  layoutKnown={d.LayoutKnown}  " +
                      $"inputReports={d.InputReports} keyboardReports={d.KeyboardReports}");
    Console.WriteLine($"   last={Convert.ToHexString(d.LastRaw)}");
    Console.WriteLine($"   {d.InstanceId}");
}
if (status.Devices.Count == 0) Console.WriteLine("No filtered keyboards.");
else if (status.Devices.Any(d => d.InputReports > 0 && !d.LayoutKnown))
    Console.WriteLine("WARNING: input seen but not in the expected layout: keys pass through unchanged. Run 'KeyProbe raw' and report the output.");
return 0;

static int Raw(DriverChannel ch, DriverStatus status, int seconds, bool force)
{
    if (status.Subscribers > 0 && !force)
    {
        Console.WriteLine("The MagicKeyboard app is connected. Exit it from the tray first (or pass --force).");
        return 1;
    }
    ch.Subscribe(actions: false, raw: true);
    Console.WriteLine($"Streaming {seconds}s. Press Fn, the Touch ID key, modifiers and some keys...");
    var sw = Stopwatch.StartNew();
    using var stop = new Timer(_ => ch.CancelPendingReads(), null, seconds * 1000, Timeout.Infinite);
    int count = 0;
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        var ev = ch.ReadEvent();
        if (ev is not { } e) break;
        if (e.Type != Protocol.EventRaw) continue;
        count++;
        var r = e.Data;
        string decoded = "";
        if (r.Length >= 10 && r[0] == 0x01)
        {
            var keys = string.Join(",", r.Skip(3).Take(6).Where(k => k != 0).Select(k => $"{k:X2}"));
            decoded = $"  mods={r[1]:X2} keys=[{keys}] fn={(r[9] >> 1) & 1} touchId={(r[9] >> 2) & 1} byte9={r[9]:X2}";
        }
        Console.WriteLine($"{sw.ElapsedMilliseconds,6}ms #{e.DeviceIndex} [{r.Length}] {Convert.ToHexString(r)}{decoded}");
    }
    Console.WriteLine($"{count} reports.");
    return 0;
}
