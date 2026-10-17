using MagicKeyboard.Shared;
using MagicKeyboard.Shared.Hid;

namespace MagicKeyboard.Gui;

// Polls the keyboard battery (KeyboardBattery: report 0x90, then Windows' Bluetooth property) every minute
// and raises Updated with the latest reading. Keeps the last known value when a read fails, so the tray
// doesn't flip to "unknown" while the keyboard is asleep.
internal sealed class BatteryMonitor : IDisposable
{
    private System.Threading.Timer? _timer;
    private int _polling;

    public event Action<BatteryStatus?>? Updated;

    public BatteryStatus? Current { get; private set; }

    public void Start() =>
        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));

    public void Refresh() => ThreadPool.QueueUserWorkItem(_ => Poll());

    public static IReadOnlyList<BatteryStatus> ReadAll()
    {
        try { return KeyboardBattery.ReadAll(); } catch { return Array.Empty<BatteryStatus>(); }
    }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;   // a slow read is still running
        try
        {
            var reading = ReadAll().FirstOrDefault();
            if (reading != null) Current = reading;
            Updated?.Invoke(Current);
        }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    public void Dispose() => _timer?.Dispose();
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
