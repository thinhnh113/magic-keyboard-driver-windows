using MagicKeyboard.Shared;
using Microsoft.Win32;

namespace MagicKeyboard.Gui;

// Same states MagicMouse shows in its tray tooltip (minus Paused, which the keyboard has no use for).
internal enum DriverState { DriverMissing, NoDevice, Running }

// Keeps a connection to the driver's control device: subscribes for key actions, pushes the current
// config, and dispatches actions to the injector. The control device only exists while a Magic Keyboard
// is attached, so this reconnects on its own after sleep, unplug or Bluetooth reconnect.
internal sealed class DriverLink : IDisposable
{
    private readonly ActionInjector _injector;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private volatile DriverChannel? _channel;
    private volatile KeyboardSettings _settings;

    public DriverLink(KeyboardSettings settings, ActionInjector injector)
    {
        _settings = settings;
        _injector = injector;
        _thread = new Thread(Run) { IsBackground = true, Name = "MagicKeyboard driver link" };
    }

    public void Start() => _thread.Start();

    public bool Connected => _channel != null;

    public DriverState State { get; private set; } = DriverState.DriverMissing;

    public event Action<DriverState>? StateChanged;

    private void SetState(DriverState s)
    {
        if (s == State) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    // The control device only exists while a keyboard is attached, so "can't open" means either the driver
    // isn't installed or no Magic Keyboard is connected. The service key tells them apart.
    private static DriverState UnreachableState()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\MagicKeyboardFilter");
        return key == null ? DriverState.DriverMissing : DriverState.NoDevice;
    }

    public DriverStatus? GetStatus()
    {
        try { return _channel?.GetStatus(); } catch { return null; }
    }

    public void PushConfig(KeyboardSettings s)
    {
        _settings = s;
        try { _channel?.SetConfig(s.ToMkConfig()); } catch { /* reconnect loop re-pushes */ }
    }

    private const int WatchdogMs = 3000;

    private void Run()
    {
        while (!_stopping)
        {
            var ch = DriverChannel.TryOpen();
            if (ch == null) { SetState(UnreachableState()); Thread.Sleep(2000); continue; }
            try
            {
                ch.Subscribe(actions: true, raw: false);
                ch.SetConfig(_settings.ToMkConfig());
                var status = ch.GetStatus();
                uint generation = status.ControlGeneration;
                _channel = ch;
                SetState(status.Devices.Count > 0 ? DriverState.Running : DriverState.NoDevice);
                AppLog.Write($"connected to driver (control device #{generation}, {status.Devices.Count} keyboard(s))");

                // The driver re-creates its control device when the keyboard is re-enumerated (e.g. after sleep).
                // If our handle is left on the old one, our read would wait forever: detect it and reconnect.
                using var watchdog = new System.Threading.Timer(_ =>
                {
                    if (!IsStale(ch, generation)) return;
                    AppLog.Write($"control device #{generation} is stale; reconnecting");
                    ch.CancelPendingReads();
                }, null, WatchdogMs, WatchdogMs);

                while (!_stopping)
                {
                    var ev = ch.ReadEvent();
                    if (ev is not { } e) break;
                    if (e.Type == Protocol.EventAction) _injector.Handle(e.Action);
                }
            }
            catch (Exception ex) { AppLog.Write("driver link: " + ex.Message); /* device went away: retry */ }
            finally
            {
                _channel = null;
                ch.Dispose();
                if (!_stopping) SetState(UnreachableState());
            }
            if (!_stopping) Thread.Sleep(1000);
        }
    }

    // Our handle is stale when it no longer reaches the current control device: either a fresh handle reports
    // a different generation, or there is no control device at all right now (no keyboard attached).
    private static bool IsStale(DriverChannel ch, uint generation)
    {
        try { if (ch.GetStatus().ControlGeneration != generation) return true; }
        catch { return true; }
        using var fresh = DriverChannel.TryOpen();
        if (fresh == null) return true;
        try { return fresh.GetStatus().ControlGeneration != generation; }
        catch { return true; }
    }

    public void Dispose()
    {
        _stopping = true;
        _channel?.Dispose();   // cancels the blocked read
    }
}
