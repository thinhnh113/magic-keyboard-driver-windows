using System.Diagnostics;
using MagicKeyboard.Shared;

namespace MagicKeyboard.Gui;

/// <summary>Settings window. Every change is applied and saved immediately. Same builders as MagicMouse.</summary>
internal sealed class SettingsForm : Form
{
    private readonly TrayContext _owner;
    private KeyboardSettings _s;
    private readonly List<Action> _loaders = new();
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(576, 0) };
    private readonly Label _battery = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f) };
    private bool _loading;

    private static readonly string[] ModifierNames =
    {
        "Left Control", "Left Shift", "Left Option (Alt)", "Left Command (Win)",
        "Right Control", "Right Shift", "Right Option (Alt)", "Right Command (Win)",
        "Caps Lock", "Fn", "Menu",
    };

    public SettingsForm(TrayContext owner)
    {
        _owner = owner;
        _s = owner.Settings;
        Text = "MagicKeyboard";
        ClientSize = new Size(680, 640);   // wider than MagicMouse: longer tab names
        MinimumSize = new Size(640, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont;
        Icon = TrayIcons.Get(32);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(KeyboardTab());
        tabs.TabPages.Add(FunctionKeysTab());
        tabs.TabPages.Add(ModifiersTab());
        tabs.TabPages.Add(SpecialKeysTab());
        tabs.TabPages.Add(DeviceTab());
        tabs.TabPages.Add(AppTab());
        Controls.Add(tabs);

        ReloadFromSettings();
        UpdateStatus();
    }

    // ------------------------------------------------------------------ tabs

    private TabPage KeyboardTab()
    {
        var page = Page("Keyboard", out var flow);
        var g = Group(flow, "Layout");
        Combo(g, "Preset", Enum.GetValues<Preset>(), s => s.Preset, (s, v) => s.ApplyPreset(v), reloadAll: true);
        Hint(g, "Windows: Command = Win, Option = Alt.  macOS: Command = Ctrl, so Cmd+C copies.  Apple: keys as labeled.");

        var b = Group(flow, "Top row and Fn");
        Check(b, "Fn lock (top row is media keys; hold Fn for F1-F19)", s => s.FnLock, (s, v) => s.FnLock = v);
        Check(b, "Sleep shortcut (Fn + Space)", s => s.SleepShortcut, (s, v) => s.SleepShortcut = v);
        Hint(b, "Fn + Return = Insert, Fn + Delete = Forward Delete, Fn + arrows = Home / End / Page Up / Page Down, " +
                "Fn + Esc = Pause/Break, Fn + P = Print Screen.");
        return page;
    }

    private TabPage FunctionKeysTab()
    {
        var page = Page("Function keys", out var flow);
        var g = Group(flow, "Top row (action in media mode)");
        var actions = Enum.GetValues<FKeyAction>();
        for (int i = 0; i < Protocol.FKeyCount; i++)
        {
            int idx = i;
            Combo(g, $"F{idx + 1}", actions,
                s => idx < s.FunctionKeys.Length ? s.FunctionKeys[idx] : FKeyAction.FKey,
                (s, v) => s.FunctionKeys[idx] = v);
        }
        return page;
    }

    private TabPage ModifiersTab()
    {
        var page = Page("Modifiers", out var flow);
        var g = Group(flow, "Modifier remapping");
        var targets = Enum.GetValues<ModTarget>();
        for (int i = 0; i < ModifierNames.Length; i++)
        {
            int idx = i;
            Combo(g, ModifierNames[idx], targets, s => s.Modifiers[idx], (s, v) => s.Modifiers[idx] = v);
        }
        var o = Group(flow, "Options");
        Check(o, "Swap Command and Option", s => s.SwapCommandOption, (s, v) => s.SwapCommandOption = v);
        Combo(o, "Third-character key (AltGr)", targets, s => s.ThirdCharacterModifier, (s, v) => s.ThirdCharacterModifier = v);
        Hint(o, "Tip: pick a preset on the Keyboard tab first, then fine-tune here.");
        return page;
    }

    private TabPage SpecialKeysTab()
    {
        var page = Page("Special keys", out var flow);
        var g = Group(flow, "Special keys");
        Combo(g, "Clear key (numeric keypad)", Enum.GetValues<ClearMode>(), s => s.ClearKey, (s, v) => s.ClearKey = v);
        Combo(g, "Touch ID / lock key", Enum.GetValues<FKeyAction>(), s => s.TouchIdKey, (s, v) => s.TouchIdKey = v);
        Hint(g, "On the 2024 Touch ID keyboard the Touch ID key sends nothing to Windows over Bluetooth; leave it at None.");
        return page;
    }

    private TabPage DeviceTab()
    {
        var page = Page("Device", out var flow);
        var info = Group(flow, "Device info");
        info.Controls.Add(_battery);
        info.Controls.Add(_status);
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        row.Controls.Add(Button("Refresh battery", () => _owner.RefreshBattery()));
        row.Controls.Add(Button("Refresh status", UpdateStatus));
        info.Controls.Add(row);

        var power = Group(flow, "Power");
        Check(power, "Disconnect the keyboard while the display is off (deep sleep; press a key to reconnect)",
            s => s.DisconnectOnDisplayOff, (s, v) => s.DisconnectOnDisplayOff = v);
        Check(power, "Disconnect the keyboard while the PC is locked",
            s => s.DisconnectOnLock, (s, v) => s.DisconnectOnLock = v);
        Hint(power, "Reconnecting takes 1-2 seconds and the key that wakes the keyboard may be lost, "
            + "so the first characters of a password can be dropped on the lock screen.");
        return page;
    }

    private TabPage AppTab()
    {
        var page = Page("App", out var flow);
        var a = Group(flow, "Application");
        Check(a, "Enabled", s => s.Enabled, (s, v) => s.Enabled = v);
        Check(a, "Start with Windows (needed for media and volume keys)", s => s.StartWithWindows, (s, v) => s.StartWithWindows = v);
        Check(a, "Show battery level in the tray icon", s => s.ShowBatteryInTray, (s, v) => s.ShowBatteryInTray = v);

        var b = Group(flow, "Battery alerts");
        Check(b, "Show battery alerts", s => s.LowBatteryAlerts, (s, v) => s.LowBatteryAlerts = v);
        Slider(b, "Warning level (%)", 5, 50, s => s.LowBatteryThreshold, (s, v) => s.LowBatteryThreshold = v);
        Slider(b, "Critical level (%)", 1, 25, s => s.CriticalBatteryThreshold, (s, v) => s.CriticalBatteryThreshold = v);
        Check(b, "Recharged alert", s => s.ChargedAlert, (s, v) => s.ChargedAlert = v);
        Slider(b, "Recharged level (%)", 50, 100, s => s.ChargedThreshold, (s, v) => s.ChargedThreshold = v);

        var adv = Group(flow, "Advanced");
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        row.Controls.Add(Button("Open settings file", () =>
            Process.Start(new ProcessStartInfo(KeyboardSettings.DefaultPath) { UseShellExecute = true })));
        row.Controls.Add(Button("Reset to defaults", () =>
        {
            if (MessageBox.Show(this, "Reset all settings to their defaults?", Text, MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            _owner.ApplySettings(new KeyboardSettings());
            ReloadFromSettings();
        }));
        adv.Controls.Add(row);
        Hint(adv, "The driver remaps keys itself and remembers these settings across reboots. This app adds media, " +
                  "volume, brightness, sleep and launcher keys, so keep it running.");
        return page;
    }

    // ------------------------------------------------------------------ status

    public void ReloadFromSettings()
    {
        _s = _owner.Settings;
        _loading = true;
        foreach (var l in _loaders) l();
        _loading = false;
    }

    public void UpdateStatus()
    {
        if (IsDisposed) return;
        var lines = new List<string>
        {
            _owner.Link.State switch
            {
                DriverState.DriverMissing => "Filter driver not installed. Reinstall MagicKeyboard.",
                DriverState.NoDevice => "Driver installed, no Magic Keyboard connected.",
                _ => "Running.",
            },
        };
        if (_owner.Link.GetStatus() is { } st)
        {
            foreach (var d in st.Devices)
                lines.Add($"Magic Keyboard (PID {d.ProductId:X4}) via {d.Transport}: " +
                          $"{(d.LayoutKnown ? "remapping" : "waiting for a keypress")}, {d.KeyboardReports} reports");
        }
        _status.Text = string.Join(Environment.NewLine, lines);
        var b = _owner.Battery;
        _battery.Text = b == null ? "Battery: unknown" :
            $"Battery: {b.Percent}%{(b.Charging == true ? " (charging)" : "")}  -  {b.Source}";
    }

    // ------------------------------------------------------------------ builders

    private static TabPage Page(string title, out FlowLayoutPanel flow)
    {
        var page = new TabPage(title) { Padding = new Padding(8) };
        flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
        };
        page.Controls.Add(flow);
        return page;
    }

    private static FlowLayoutPanel Group(FlowLayoutPanel parent, string title)
    {
        var box = new GroupBox { Text = title, AutoSize = true, Padding = new Padding(10, 6, 10, 8), MinimumSize = new Size(596, 0) };
        var inner = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        box.Controls.Add(inner);
        parent.Controls.Add(box);
        return inner;
    }

    private static void Hint(FlowLayoutPanel g, string text) =>
        g.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(576, 0), ForeColor = SystemColors.GrayText });

    private void Check(FlowLayoutPanel g, string text, Func<KeyboardSettings, bool> get, Action<KeyboardSettings, bool> set)
    {
        var cb = new CheckBox { Text = text, AutoSize = true };
        cb.CheckedChanged += (_, _) => Change(s => set(s, cb.Checked));
        _loaders.Add(() => cb.Checked = get(_s));
        g.Controls.Add(cb);
    }

    private void Slider(FlowLayoutPanel g, string text, int min, int max, Func<KeyboardSettings, int> get, Action<KeyboardSettings, int> set)
    {
        var label = new Label { AutoSize = true };
        var tb = new TrackBar { Minimum = min, Maximum = max, Width = 380, TickFrequency = Math.Max(1, (max - min) / 10), AutoSize = true };
        void Caption() => label.Text = $"{text}: {tb.Value}";
        tb.ValueChanged += (_, _) => { Caption(); Change(s => set(s, tb.Value)); };
        _loaders.Add(() => { tb.Value = Math.Clamp(get(_s), min, max); Caption(); });
        g.Controls.Add(label);
        g.Controls.Add(tb);
    }

    private void Combo<T>(FlowLayoutPanel g, string text, T[] values, Func<KeyboardSettings, T> get,
                          Action<KeyboardSettings, T> set, bool reloadAll = false) where T : struct, Enum
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        cb.Items.AddRange(values.Select(v => (object)Display(v)).ToArray());
        cb.SelectedIndexChanged += (_, _) =>
        {
            if (cb.SelectedIndex < 0) return;
            Change(s => set(s, values[cb.SelectedIndex]));
            if (reloadAll && !_loading) ReloadFromSettings();   // e.g. a preset rewrites the modifier table
        };
        _loaders.Add(() => cb.SelectedIndex = Array.IndexOf(values, get(_s)));
        // Auto-sized like MagicMouse's labels (a fixed height clips the text at high DPI); the minimum
        // width lines the dropdowns up in a column.
        row.Controls.Add(new Label { Text = text, AutoSize = true, MinimumSize = new Size(230, 0), Padding = new Padding(0, 6, 0, 0) });
        row.Controls.Add(cb);
        g.Controls.Add(row);
    }

    private static Button Button(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void Change(Action<KeyboardSettings> mutate)
    {
        if (_loading) return;
        mutate(_s);
        _owner.ApplySettings(_s);
    }

    // Modifier targets use the key labels (HID calls Command "GUI"); everything else is split from PascalCase.
    private static string Display<T>(T value) where T : struct, Enum => value switch
    {
        ModTarget.LeftGui => "Left Command (Win)",
        ModTarget.RightGui => "Right Command (Win)",
        ModTarget.LeftAlt => "Left Option (Alt)",
        ModTarget.RightAlt => "Right Option (Alt)",
        _ => Friendly(value.ToString()),
    };

    // "BrightnessDown" -> "Brightness Down"
    private static string Friendly(string pascal)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i]) && !char.IsUpper(pascal[i - 1])) sb.Append(' ');
            sb.Append(pascal[i]);
        }
        return sb.ToString();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.CloseReason == CloseReason.UserClosing)
        {
            // Keep running in the tray.
            e.Cancel = true;
            Hide();
        }
    }
}
