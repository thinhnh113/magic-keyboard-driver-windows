using MagicKeyboard.Shared;
using MagicKeyboard.Shared.Hid;

namespace MagicKeyboard.Gui;

/// <summary>Tray icon, battery monitor and driver link. Same shape as MagicMouse's TrayContext.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly MessageWindow _messages;
    private readonly BatteryMonitor _battery = new();
    private readonly BatteryAlerts _alerts = new();
    private readonly ActionInjector _injector;
    private readonly DriverLink _link;
    private readonly SynchronizationContext _ui;
    private SettingsForm? _form;

    public TrayContext(bool startMinimized)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_ui);
        Settings = KeyboardSettings.Load();
        if (!File.Exists(KeyboardSettings.DefaultPath)) Settings.Save();
        // Keep the startup entry current (also migrates the old "MagicKeyboardReborn" name).
        Autostart.Apply(Settings.StartWithWindows);

        _enabledItem = new ToolStripMenuItem("Enabled", null, (_, _) => ToggleEnabled()) { Checked = Settings.Enabled };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add(_enabledItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "MagicKeyboard" };
        _tray.DoubleClick += (_, _) => ShowSettings();
        _messages = new MessageWindow(this);

        // Driver connection: pushes config and performs media/brightness/launcher actions.
        _injector = new ActionInjector(Settings);
        _link = new DriverLink(Settings, _injector);
        _link.StateChanged += _ => UiInvoke(() => { UpdateTray(); _form?.UpdateStatus(); });
        _link.Start();

        _battery.Updated += b => UiInvoke(() => OnBattery(b));
        _battery.Start();

        UpdateTray();
        if (!startMinimized) ShowSettings();
    }

    public KeyboardSettings Settings { get; private set; }
    public DriverLink Link => _link;
    public BatteryStatus? Battery => _battery.Current;

    // Every settings change: save, push to the driver (which persists it for boot), refresh the tray.
    public void ApplySettings(KeyboardSettings s)
    {
        Settings = s;
        Settings.Save();
        _link.PushConfig(s);
        _injector.UpdateSettings(s);
        Autostart.Apply(s.StartWithWindows);
        _enabledItem.Checked = s.Enabled;
        UpdateTray();
    }

    private void ToggleEnabled()
    {
        Settings.Enabled = !Settings.Enabled;
        ApplySettings(Settings);
        _form?.ReloadFromSettings();
    }

    public void ShowSettings()
    {
        if (_form == null || _form.IsDisposed)
        {
            _form = new SettingsForm(this);
            _form.FormClosed += (_, _) => _form = null;
        }
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    // ------------------------------------------------------------------ battery

    public void RefreshBattery() => _battery.Refresh();

    private void OnBattery(BatteryStatus? b)
    {
        if (b != null && _alerts.Check(Settings, b) is { } a)
            _tray.ShowBalloonTip(8000, a.Title, a.Text, a.Critical ? ToolTipIcon.Error : ToolTipIcon.Warning);
        UpdateTray();
        _form?.UpdateStatus();
    }

    // ------------------------------------------------------------------ tray icon

    // Tooltip and icon follow MagicMouse's template, e.g. "MagicKeyboard: active - 19%".
    private void UpdateTray()
    {
        string state = _link.State switch
        {
            DriverState.DriverMissing => "driver not installed",
            DriverState.NoDevice => "no Magic Keyboard",
            _ => Settings.Enabled ? "active" : "disabled",
        };
        var b = _battery.Current;
        string battery = b is { } s ? $" - {s.Percent}%{(s.Charging == true ? " (charging)" : "")}" : "";
        var text = $"MagicKeyboard: {state}{battery}";
        _tray.Text = text.Length > 63 ? text[..63] : text;

        var old = _tray.Icon;
        _tray.Icon = TrayIcons.Build(_link.State == DriverState.Running && Settings.Enabled,
                                     Settings.ShowBatteryInTray ? b?.Percent : null);
        old?.Dispose();
    }

    private void UiInvoke(Action a) => _ui.Post(_ => a(), null);

    protected override void ExitThreadCore()
    {
        _battery.Dispose();
        _link.Dispose();
        _injector.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _messages.Dispose();
        _form?.Dispose();   // Close() would only hide it
        base.ExitThreadCore();
    }

    /// <summary>Hidden window: "show" requests from a second instance.</summary>
    private sealed class MessageWindow : NativeWindow, IDisposable
    {
        private readonly TrayContext _owner;

        public MessageWindow(TrayContext owner)
        {
            _owner = owner;
            // Top-level (not message-only) so HWND_BROADCAST from a second instance reaches it.
            CreateHandle(new CreateParams { Caption = "MagicKeyboard.Messages" });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeUi.ShowMessage) _owner.ShowSettings();
            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();
    }
}
