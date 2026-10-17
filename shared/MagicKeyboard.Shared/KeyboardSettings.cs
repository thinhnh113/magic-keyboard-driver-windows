using System.Text.Json;
using System.Text.Json.Serialization;

namespace MagicKeyboard.Shared;

// The user-facing settings model the GUI edits. Persisted as JSON and projected
// to the flat MkConfig the driver consumes. Defaults reproduce the documented
// Magic Keyboard Utilities behaviour.

public sealed class KeyboardSettings
{
    public bool Enabled { get; set; } = true;
    public Preset Preset { get; set; } = Preset.Windows;

    // Top row is media by default; press Fn to reach F-keys.
    public bool FnLock { get; set; } = true;
    public bool SleepShortcut { get; set; } = true;      // fn + space
    public bool SwapCommandOption { get; set; } = false;
    public ClearMode ClearKey { get; set; } = ClearMode.NumLock;
    // Touch ID / lock key. Off by default until its report bit is confirmed on your keyboard.
    public FKeyAction TouchIdKey { get; set; } = FKeyAction.None;
    public ModTarget ThirdCharacterModifier { get; set; } = ModTarget.Disabled;

    public int LowBatteryThreshold { get; set; } = 15;
    public int CriticalBatteryThreshold { get; set; } = 5;
    public int ChargedThreshold { get; set; } = 80;
    public bool ChargedAlert { get; set; } = true;
    public bool LowBatteryAlerts { get; set; } = true;
    public bool ShowBatteryInTray { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;

    // Per-top-row media action (index 0 = F1).
    public FKeyAction[] FunctionKeys { get; set; } = DefaultFunctionKeys();

    // Custom launcher command lines for LaunchCustom slots (index = CustomSlot).
    public List<string> CustomLaunchers { get; set; } = new();

    // Modifier remap, indexed by ModSlot.
    public ModTarget[] Modifiers { get; set; } = WindowsModifiers();

    // --- documented defaults -------------------------------------------------

    public static FKeyAction[] DefaultFunctionKeys() => new[]
    {
        FKeyAction.BrightnessDown, // F1
        FKeyAction.BrightnessUp,   // F2
        FKeyAction.TaskView,       // F3
        FKeyAction.Search,         // F4 (2021 models)
        FKeyAction.Dictation,      // F5 (2021 models)
        FKeyAction.Sleep,          // F6 (2021 models)
        FKeyAction.PrevTrack,      // F7
        FKeyAction.PlayPause,      // F8
        FKeyAction.NextTrack,      // F9
        FKeyAction.Mute,           // F10
        FKeyAction.VolumeDown,     // F11
        FKeyAction.VolumeUp,       // F12
        FKeyAction.PrintScreen,    // F13
        FKeyAction.ScrollLock,     // F14
        FKeyAction.PauseBreak,     // F15
        FKeyAction.LaunchExplorer, // F16
        FKeyAction.LaunchCalc,     // F17
        FKeyAction.LaunchNotepad,  // F18
        FKeyAction.LaunchTaskMgr   // F19
    };

    // Windows preset: Command->Win, Option->Alt (natural Windows feel).
    public static ModTarget[] WindowsModifiers() => new[]
    {
        ModTarget.LeftControl,  // LeftControl
        ModTarget.LeftShift,    // LeftShift
        ModTarget.LeftAlt,      // LeftAlt   (phys Option)
        ModTarget.LeftGui,      // LeftGui   (phys Command)
        ModTarget.RightControl, // RightControl
        ModTarget.RightShift,   // RightShift
        ModTarget.RightAlt,     // RightAlt  (phys Option)
        ModTarget.RightGui,     // RightGui  (phys Command)
        ModTarget.CapsLock,     // CapsLock
        ModTarget.Fn,           // Fn
        ModTarget.Menu          // Menu
    };

    // macOS preset: Command->Ctrl, Control->Win (so Cmd+C etc. map to Ctrl+C).
    public static ModTarget[] MacOsModifiers() => new[]
    {
        ModTarget.LeftGui,      // LeftControl -> Win
        ModTarget.LeftShift,
        ModTarget.LeftAlt,
        ModTarget.LeftControl,  // LeftCommand -> Ctrl
        ModTarget.RightGui,
        ModTarget.RightShift,
        ModTarget.RightAlt,
        ModTarget.RightControl, // RightCommand -> Ctrl
        ModTarget.CapsLock,
        ModTarget.Fn,
        ModTarget.Menu
    };

    // Apple preset: 1:1 physical labels (Command=Win, Option=Alt), Apple-native.
    public static ModTarget[] AppleModifiers() => WindowsModifiers();

    public void ApplyPreset(Preset p)
    {
        Preset = p;
        Modifiers = p switch
        {
            Preset.MacOS => MacOsModifiers(),
            Preset.Apple => AppleModifiers(),
            _ => WindowsModifiers()
        };
    }

    // --- projection to the driver struct ------------------------------------

    public MkConfig ToMkConfig()
    {
        var c = MkConfig.CreateEmpty();
        c.Enabled = (byte)(Enabled ? 1 : 0);
        c.Preset = (byte)Preset;
        c.FnLock = (byte)(FnLock ? 1 : 0);
        c.SleepShortcut = (byte)(SleepShortcut ? 1 : 0);
        c.ClearMode = (byte)ClearKey;
        c.TouchIdAction = (byte)TouchIdKey;
        c.SwapCmdOption = (byte)(SwapCommandOption ? 1 : 0);
        c.ThirdCharMod = (byte)ThirdCharacterModifier;

        for (int i = 0; i < Protocol.FKeyCount; i++)
        {
            var action = i < FunctionKeys.Length ? FunctionKeys[i] : FKeyAction.FKey;
            c.FKeys[i] = new FKeySlot { Action = (byte)action, CustomSlot = 0 };
        }
        for (int i = 0; i < Protocol.ModSlotCount; i++)
            c.Modifiers[i] = (byte)(i < Modifiers.Length ? Modifiers[i] : ModTarget.Disabled);
        return c;
    }

    // --- persistence ---------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "MagicKeyboard", "settings.json");

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    public static KeyboardSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<KeyboardSettings>(
                    File.ReadAllText(path), JsonOpts) ?? new KeyboardSettings();
        }
        catch { /* fall through to defaults on any corruption */ }
        return new KeyboardSettings();
    }
}
