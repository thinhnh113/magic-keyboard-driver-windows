using MagicKeyboard.Shared;
using MagicKeyboard.Shared.Hid;

namespace MagicKeyboard.Gui;

// Reads the keyboard battery (KeyboardBattery: report 0x90, then Windows' Bluetooth property) and raises
// Updated with the latest reading. Keeps the last known value when a read fails, so the tray doesn't flip
// to "unknown" while the keyboard is asleep.
// Each read over Bluetooth briefly wakes the keyboard's radio, and the level moves about 1% a day, so it
// reads every 15 minutes, and not at all while Paused (display off / locked). It reads every minute while
// charging (costs the battery nothing) and every 2 minutes at or below the warning level, when the cable is
// likely to be plugged in soon. Refresh() reads right away, e.g. after wake or unlock.
// The keyboard also sends report 0x90 by itself when its battery changes (seen: 90 03 0A while charging),
// so a listener on the battery collection picks those up the moment they arrive, at no cost to the keyboard.
internal sealed class BatteryMonitor : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ChargingInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LowInterval = TimeSpan.FromMinutes(2);

    private System.Threading.Timer? _timer;
    private int _polling;
    private DateTime _lastPoll = DateTime.MinValue;
    private volatile bool _disposed;

    public event Action<BatteryStatus?>? Updated;

    public BatteryStatus? Current { get; private set; }

    public bool Paused { get; set; }

    public Func<int> LowLevel { get; set; } = () => 15;

    public void Start()
    {
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.Zero, ChargingInterval);
        new Thread(Listen) { IsBackground = true, Name = "battery listener" }.Start();
    }

    public void Refresh() => ThreadPool.QueueUserWorkItem(_ => Poll());

    public static IReadOnlyList<BatteryStatus> ReadAll()
    {
        try { return KeyboardBattery.ReadAll(); } catch { return Array.Empty<BatteryStatus>(); }
    }

    private void Tick()
    {
        if (Paused) return;
        var due = Current?.Charging == true ? ChargingInterval
                : Current?.Percent <= LowLevel() ? LowInterval
                : Interval;
        if (DateTime.UtcNow - _lastPoll >= due - TimeSpan.FromSeconds(5)) Poll();
    }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;   // a slow read is still running
        try
        {
            _lastPoll = DateTime.UtcNow;
            var reading = ReadAll().FirstOrDefault();
            if (reading != null) Current = reading;
            Updated?.Invoke(Current);
        }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    private void Listen()
    {
        while (!_disposed)
        {
            var col = SafeEnumerate().FirstOrDefault(c => c.UsagePage == 0xFF00 && c.Usage == 0x14 && c.InputLength >= 3);
            if (col != null)
            {
                try
                {
                    using var h = KeyboardBattery.OpenForReading(col);
                    if (!h.IsInvalid)
                    {
                        using var fs = new FileStream(h, FileAccess.Read, 0);
                        var buf = new byte[col.InputLength];
                        // Ends with an exception when the keyboard disconnects.
                        while (!_disposed && fs.Read(buf, 0, buf.Length) > 0)
                        {
                            if (KeyboardBattery.Decode(string.IsNullOrWhiteSpace(col.Product) ? "Magic Keyboard" : col.Product, buf) is not { } b)
                                continue;
                            b = b with { Source = "HID report 0x90 (sent by the keyboard)" };
                            Current = b;
                            Updated?.Invoke(b);
                        }
                    }
                }
                catch { /* disconnected: look for it again */ }
            }
            Thread.Sleep(10_000);
        }
    }

    private static List<HidCollection> SafeEnumerate()
    {
        try { return KeyboardBattery.Enumerate(KeyboardBattery.IsMagicKeyboard).ToList(); }
        catch { return new List<HidCollection>(); }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
    }
}

// Low / critical / recharged balloons, fired once per crossing (same rules as MagicMouse).
internal sealed class BatteryAlerts
{
    private int _lastLevel = int.MaxValue;

    public (string Title, string Text, bool Critical)? Check(KeyboardSettings s, BatteryStatus b)
    {
        int p = b.Percent;
        (string, string, bool)? alert = null;
        if (s.LowBatteryAlerts)
        {
            if (p <= s.CriticalBatteryThreshold && _lastLevel > s.CriticalBatteryThreshold)
                alert = ("Magic Keyboard battery critical", $"{p}% left. Charge the keyboard now.", true);
            else if (p <= s.LowBatteryThreshold && _lastLevel > s.LowBatteryThreshold)
                alert = ("Magic Keyboard battery low", $"{p}% left.", false);
            else if (s.ChargedAlert && p >= s.ChargedThreshold && _lastLevel < s.ChargedThreshold && _lastLevel != int.MaxValue)
                alert = ("Magic Keyboard charged", $"Battery at {p}%.", false);
        }
        _lastLevel = p;
        return alert;
    }
}
