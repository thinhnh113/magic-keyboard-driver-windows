using System.IO;
using System.Threading;
using System.Windows;
using MagicKeyboard.Shared;
using MagicKeyboard.Shared.Hid;
using WinForms = System.Windows.Forms;

namespace MagicKeyboard.Gui;

// Tray-first application. The settings window is created on demand; the app
// lives in the notification area and keeps running when the window is closed.
// A manual launch shows the window; launching with --minimized starts silently
// to the tray. A second launch focuses the first instance instead of starting a
// new one.

public partial class App : Application
{
    private const string MutexName = @"Local\MagicKeyboard.Gui.Instance";
    private const string ShowEventName = @"Local\MagicKeyboard.Gui.Show";

    private WinForms.NotifyIcon _tray = null!;
    private MainWindow? _window;
    private BatteryMonitor? _battery;
    private Mutex? _mutex;
    private ActionInjector? _injector;
    private readonly BatteryAlerts _alerts = new();
    public KeyboardSettings Settings { get; private set; } = KeyboardSettings.Load();
    internal DriverLink? Link { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            WinForms.MessageBox.Show(ex.Exception.ToString(), "MagicKeyboard - error",
                WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
            ex.Handled = true;
        };

        // Single-instance: if we're not the first, signal the running instance
        // to show its window, then exit.
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* ignore */ }
            Shutdown();
            return;
        }

        // Background listener: a later launch sets this event; we show the window.
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (true)
            {
                showEvent.WaitOne();
                Dispatcher.Invoke(ShowSettings);
            }
        }) { IsBackground = true };
        listener.Start();

        // --minimized (the installer's and MagicMouse's flag); --autostart from earlier builds.
        bool silent = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
                                      a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

        _tray = new WinForms.NotifyIcon { Visible = true };
        _tray.DoubleClick += (_, _) => ShowSettings();
        _tray.ContextMenuStrip = BuildMenu();
        UpdateTray();

        _battery = new BatteryMonitor();
        _battery.Updated += b => Dispatcher.BeginInvoke(() => OnBattery(b));
        _battery.Start();

        // Driver connection: pushes config and performs media/brightness/launcher actions.
        _injector = new ActionInjector(Settings);
        Link = new DriverLink(Settings, _injector);
        Link.StateChanged += _ => Dispatcher.BeginInvoke(UpdateTray);
        Link.Start();

        // Keep the startup entry current (also migrates the old "MagicKeyboardReborn" name).
        Autostart.Apply(Settings.StartWithWindows);

        if (!File.Exists(KeyboardSettings.DefaultPath))
            Settings.Save();

        // Show the window on a normal launch; --minimized starts silent.
        if (!silent)
            ShowSettings();
    }

    private WinForms.ContextMenuStrip BuildMenu()
    {
        var menu = new WinForms.ContextMenuStrip();
        var toggle = new WinForms.ToolStripMenuItem("Enabled") { Checked = Settings.Enabled, CheckOnClick = true };
        toggle.Click += (_, _) => { Settings.Enabled = toggle.Checked; Persist(); };
        menu.Items.Add(toggle);
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        return menu;
    }

    public void ShowSettings()
    {
        if (_window == null)
        {
            _window = new MainWindow(Settings, Persist);
            _window.Closed += (_, _) => _window = null;
        }
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    // Called whenever settings change: save to disk, push to the driver (which persists it for boot),
    // and refresh the tray.
    public void Persist()
    {
        Settings.Save();
        Link?.PushConfig(Settings);
        _injector?.UpdateSettings(Settings);
        UpdateTray();
        Autostart.Apply(Settings.StartWithWindows);
    }

    public BatteryStatus? Battery => _battery?.Current;

    public void RefreshBattery() => _battery?.Refresh();

    private void OnBattery(BatteryStatus? b)
    {
        if (b != null && _alerts.Check(Settings, b) is { } a)
            _tray.ShowBalloonTip(8000, a.Title, a.Text, a.Critical ? WinForms.ToolTipIcon.Error : WinForms.ToolTipIcon.Warning);
        UpdateTray();
        _window?.UpdateBattery(b);
    }

    // Tooltip and icon follow MagicMouse's TrayContext.UpdateTray template, e.g. "MagicKeyboard: active - 19%".
    private void UpdateTray()
    {
        var driver = Link?.State ?? DriverState.DriverMissing;
        string state = driver switch
        {
            DriverState.DriverMissing => "driver not installed",
            DriverState.NoDevice => "no Magic Keyboard",
            _ => Settings.Enabled ? "active" : "disabled",
        };
        var b = _battery?.Current;
        string battery = b is { } s ? $" - {s.Percent}%{(s.Charging == true ? " (charging)" : "")}" : "";
        var text = $"MagicKeyboard: {state}{battery}";
        _tray.Text = text.Length > 63 ? text[..63] : text;

        var old = _tray.Icon;
        _tray.Icon = TrayIcons.Build(driver == DriverState.Running && Settings.Enabled,
                                     Settings.ShowBatteryInTray ? b?.Percent : null);
        old?.Dispose();
    }

    private void ExitApp()
    {
        _battery?.Dispose();
        Link?.Dispose();
        _injector?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        Shutdown();
    }
}
