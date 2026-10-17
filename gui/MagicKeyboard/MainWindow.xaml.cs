using System.Windows;
using System.Windows.Controls;
using MagicKeyboard.Shared;

namespace MagicKeyboard.Gui;

public partial class MainWindow : Window
{
    private readonly KeyboardSettings _s;
    private readonly Action _persist;
    private bool _loading;

    public MainWindow(KeyboardSettings settings, Action persist)
    {
        _s = settings;
        _persist = persist;
        InitializeComponent();

        CloseBtn.Click += (_, _) => Hide();
        ResetBtn.Click += (_, _) => ResetDefaults();

        BuildGeneral();
        BuildFKeys();
        BuildModifiers();
        BuildSpecial();
        BuildBattery();
        BuildAbout();
        RefreshStatus();
    }

    private void Changed()
    {
        if (_loading) return;
        _persist();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var st = (Application.Current as App)?.Link?.GetStatus();
        string driver = st == null
            ? "Driver not detected (install it, or turn on the keyboard)"
            : $"Driver connected · {st.Devices.Count} keyboard(s)" +
              (st.Devices.Any(d => !d.LayoutKnown) ? " · press a key to verify layout" : "");
        StatusLine.Text = $"{(_s.Enabled ? "Enabled" : "Disabled")} · {_s.Preset} preset · {driver}";
    }

    // ---- builders -----------------------------------------------------------

    private void BuildGeneral()
    {
        _loading = true;
        var g = GeneralPanel;
        var box = Group("General");
        box.Add(Check("Enable Magic Keyboard remapping", _s.Enabled, v => { _s.Enabled = v; Changed(); }));
        box.Add(Hint("Master switch. When off, keys pass through untouched."));
        box.Add(Combo("Preset", EnumPairs<Preset>(), _s.Preset, v => { _s.ApplyPreset(v); ReloadDynamic(); Changed(); }));
        box.Add(Hint("Windows = Command→Win, Option→Alt. macOS = Command→Ctrl. Apple = native labels."));
        g.Children.Add(box.Box);

        var fn = Group("Behaviour");
        fn.Add(Check("Fn lock (top row acts as media keys; press Fn for F1–F19)", _s.FnLock, v => { _s.FnLock = v; Changed(); }));
        fn.Add(Check("Sleep shortcut (Fn + Space)", _s.SleepShortcut, v => { _s.SleepShortcut = v; Changed(); }));
        fn.Add(Check("Start with Windows", _s.StartWithWindows, v => { _s.StartWithWindows = v; Changed(); }));
        g.Children.Add(fn.Box);
        _loading = false;
    }

    private void BuildFKeys()
    {
        _loading = true;
        FKeysPanel.Children.Clear();
        var box = Group("Top-row keys (action when in media mode)");
        var pairs = FKeyActionPairs();
        for (int i = 0; i < Protocol.FKeyCount; i++)
        {
            int idx = i;
            var cur = idx < _s.FunctionKeys.Length ? _s.FunctionKeys[idx] : FKeyAction.FKey;
            box.Add(Combo($"F{idx + 1}", pairs, cur, v =>
            {
                _s.FunctionKeys[idx] = v; Changed();
            }));
        }
        FKeysPanel.Children.Add(box.Box);
        _loading = false;
    }

    private void BuildModifiers()
    {
        _loading = true;
        ModsPanel.Children.Clear();
        var box = Group("Modifier remapping");
        var pairs = EnumPairs<ModTarget>();
        string[] names = { "Left Control", "Left Shift", "Left Option (Alt)", "Left Command (Win)",
            "Right Control", "Right Shift", "Right Option (Alt)", "Right Command (Win)",
            "Caps Lock", "Fn", "Menu" };
        for (int i = 0; i < names.Length && i < _s.Modifiers.Length; i++)
        {
            int idx = i;
            box.Add(Combo(names[i], pairs, _s.Modifiers[idx], v => { _s.Modifiers[idx] = v; Changed(); }));
        }
        ModsPanel.Children.Add(box.Box);
        ModsPanel.Children.Add(Hint("Tip: pick a preset on the General tab for a one-click starting point, then fine-tune here."));
        _loading = false;
    }

    private void BuildSpecial()
    {
        _loading = true;
        SpecialPanel.Children.Clear();
        var box = Group("Special keys");
        box.Add(Combo("Clear key (numeric keypad)", EnumPairs<ClearMode>(), _s.ClearKey, v => { _s.ClearKey = v; Changed(); }));
        box.Add(Combo("Touch ID / lock key", FKeyActionPairs(), _s.TouchIdKey, v => { _s.TouchIdKey = v; Changed(); }));
        box.Add(Hint("Experimental: the key's report bit is not yet confirmed on every model. Leave at None if unsure."));
        box.Add(Combo("Third-character modifier (AltGr)", EnumPairs<ModTarget>(), _s.ThirdCharacterModifier, v => { _s.ThirdCharacterModifier = v; Changed(); }));
        box.Add(Check("Swap Command and Option", _s.SwapCommandOption, v => { _s.SwapCommandOption = v; Changed(); }));
        SpecialPanel.Children.Add(box.Box);
        SpecialPanel.Children.Add(Hint("Missing keys are produced with Fn: Fn+Return=Insert, Fn+Delete=Forward Delete, " +
            "Fn+Arrows=Home/End/PgUp/PgDn, Fn+Esc=Pause/Break."));
        _loading = false;
    }

    private void BuildBattery()
    {
        _loading = true;
        BatteryPanel.Children.Clear();
        var status = Group("Status");
        _batteryText = new TextBlock { FontSize = 15, Margin = new Thickness(0, 2, 0, 6) };
        status.Add(_batteryText);
        var readBtn = new Button { Content = "Refresh", Width = 100, Height = 28, HorizontalAlignment = HorizontalAlignment.Left };
        readBtn.Click += (_, _) => { _batteryText.Text = "Reading…"; (Application.Current as App)?.RefreshBattery(); };
        status.Add(readBtn);
        BatteryPanel.Children.Add(status.Box);
        UpdateBattery((Application.Current as App)?.Battery);

        var box = Group("Tray and alerts");
        box.Add(Check("Show battery level in the tray icon", _s.ShowBatteryInTray, v => { _s.ShowBatteryInTray = v; Changed(); }));
        box.Add(Check("Battery alerts", _s.LowBatteryAlerts, v => { _s.LowBatteryAlerts = v; Changed(); }));
        box.Add(Slider("Low warning at", 5, 50, _s.LowBatteryThreshold, v => _s.LowBatteryThreshold = v));
        box.Add(Slider("Critical warning at", 1, 20, _s.CriticalBatteryThreshold, v => _s.CriticalBatteryThreshold = v));
        box.Add(Check("Notify when charged", _s.ChargedAlert, v => { _s.ChargedAlert = v; Changed(); }));
        box.Add(Slider("Charged at", 50, 100, _s.ChargedThreshold, v => _s.ChargedThreshold = v));
        BatteryPanel.Children.Add(box.Box);
        _loading = false;
    }

    private TextBlock? _batteryText;

    // Called by the app whenever a new battery reading arrives.
    public void UpdateBattery(MagicKeyboard.Shared.Hid.BatteryStatus? b)
    {
        if (_batteryText == null) return;
        _batteryText.Text = b == null
            ? "Battery: unknown (keyboard off, asleep, or not connected)"
            : $"{b.Device}: {b.Percent}%{(b.Charging == true ? " (charging)" : "")}";
    }

    private DockPanel Slider(string label, int min, int max, int value, Action<int> set)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var lbl = new TextBlock { Text = $"{label} {value}%", Width = 220, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 300 };
        slider.ValueChanged += (_, e) => { set((int)e.NewValue); lbl.Text = $"{label} {(int)e.NewValue}%"; Changed(); };
        DockPanel.SetDock(lbl, Dock.Left);
        row.Children.Add(lbl);
        row.Children.Add(slider);
        return row;
    }

    private void BuildAbout()
    {
        AboutPanel.Children.Clear();
        AboutPanel.Children.Add(new TextBlock { Text = "MagicKeyboard", FontSize = 16, FontWeight = FontWeights.SemiBold });
        AboutPanel.Children.Add(new TextBlock { Text = "A clean-room reimplementation of Magic Keyboard Utilities for Windows.", Margin = new Thickness(0, 4, 0, 10), TextWrapping = TextWrapping.Wrap });
        AboutPanel.Children.Add(Hint("Settings are stored at:\n" + KeyboardSettings.DefaultPath));
        AboutPanel.Children.Add(Hint("The filter driver remaps keys itself and remembers your settings across reboots. " +
            "This app adds media, volume, brightness, sleep and launcher keys, so keep it running (Start with Windows). " +
            "If the driver is not detected, see driver\\MagicKeyboardFilter\\install.cmd."));
    }

    private void ReloadDynamic()
    {
        BuildModifiers();
    }

    private void ResetDefaults()
    {
        _loading = true;
        var def = new KeyboardSettings();
        _s.Enabled = def.Enabled; _s.Preset = def.Preset; _s.FnLock = def.FnLock;
        _s.SleepShortcut = def.SleepShortcut; _s.SwapCommandOption = def.SwapCommandOption;
        _s.ClearKey = def.ClearKey; _s.TouchIdKey = def.TouchIdKey; _s.ThirdCharacterModifier = def.ThirdCharacterModifier;
        _s.LowBatteryThreshold = def.LowBatteryThreshold; _s.LowBatteryAlerts = def.LowBatteryAlerts;
        _s.StartWithWindows = def.StartWithWindows;
        _s.CriticalBatteryThreshold = def.CriticalBatteryThreshold; _s.ChargedThreshold = def.ChargedThreshold;
        _s.ChargedAlert = def.ChargedAlert; _s.ShowBatteryInTray = def.ShowBatteryInTray;
        _s.FunctionKeys = (FKeyAction[])def.FunctionKeys.Clone();
        _s.Modifiers = (ModTarget[])def.Modifiers.Clone();
        _loading = false;
        BuildGeneral(); BuildFKeys(); BuildModifiers(); BuildSpecial(); BuildBattery();
        Changed();
    }

    // ---- small control helpers ---------------------------------------------

    private sealed class Section
    {
        public GroupBox Box = null!;
        public StackPanel Panel = null!;
        public void Add(UIElement e) => Panel.Children.Add(e);
    }

    private Section Group(string header)
    {
        var panel = new StackPanel();
        var box = new GroupBox { Header = header, Content = panel };
        return new Section { Box = box, Panel = panel };
    }

    private CheckBox Check(string text, bool value, Action<bool> set)
    {
        var cb = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };
        cb.Checked += (_, _) => set(true);
        cb.Unchecked += (_, _) => set(false);
        return cb;
    }

    private TextBlock Hint(string text) => new() { Text = text, Style = (Style)Resources["Hint"] };

    private DockPanel Combo<T>(string label, List<KeyValuePair<string, T>> items, T current, Action<T> set)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var lbl = new TextBlock { Text = label, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        var combo = new ComboBox
        {
            Width = 300,
            HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = items,
            DisplayMemberPath = "Key",
            SelectedValuePath = "Value",
            SelectedValue = current
        };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedValue is T v) set(v); };
        DockPanel.SetDock(lbl, Dock.Left);
        row.Children.Add(lbl);
        row.Children.Add(combo);
        return row;
    }

    private static List<KeyValuePair<string, T>> EnumPairs<T>() where T : struct, Enum =>
        Enum.GetValues<T>().Select(v => new KeyValuePair<string, T>(Friendly(v.ToString()), v)).ToList();

    private static List<KeyValuePair<string, FKeyAction>> FKeyActionPairs() =>
        Enum.GetValues<FKeyAction>().Select(v => new KeyValuePair<string, FKeyAction>(Friendly(v.ToString()), v)).ToList();

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
}
