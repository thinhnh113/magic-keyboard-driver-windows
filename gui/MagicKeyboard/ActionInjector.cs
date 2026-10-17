using System.Diagnostics;
using System.Runtime.InteropServices;
using MagicKeyboard.Shared;

namespace MagicKeyboard.Gui;

// Performs the key actions the driver hands to user mode: media and volume keys (SendInput, so the
// Windows volume/media flyout appears), rewind/fast-forward (WM_APPCOMMAND), brightness, sleep and
// launchers. Volume and brightness repeat while the key is held, like a hardware key would.
internal sealed class ActionInjector : IDisposable
{
    private const int RepeatDelayMs = 400, RepeatRateMs = 80;

    private KeyboardSettings _settings;
    private readonly object _gate = new();
    private readonly Dictionary<FKeyAction, Timer> _repeats = new();

    public ActionInjector(KeyboardSettings settings) => _settings = settings;

    public void UpdateSettings(KeyboardSettings s) => _settings = s;

    public void Handle(KeyAction a)
    {
        ushort vk = MediaVk(a.Action);
        if (vk != 0)
        {
            SendKey(vk, a.Down);
            if (a.Action is FKeyAction.VolumeUp or FKeyAction.VolumeDown) Repeat(a, () => SendKey(vk, true));
            return;
        }

        switch (a.Action)
        {
            case FKeyAction.BrightnessUp:
            case FKeyAction.BrightnessDown:
            {
                int step = a.Action == FKeyAction.BrightnessUp ? 10 : -10;
                if (a.Down) Task.Run(() => Brightness.Step(step));
                Repeat(a, () => Brightness.Step(step));
                break;
            }
            case FKeyAction.Rewind when a.Down: AppCommand(APPCOMMAND_MEDIA_REWIND); break;
            case FKeyAction.FastForward when a.Down: AppCommand(APPCOMMAND_MEDIA_FAST_FORWARD); break;
            case FKeyAction.Sleep when a.Down: Task.Run(() => SetSuspendState(false, false, false)); break;
            case FKeyAction.LaunchExplorer when a.Down: Launch("explorer.exe"); break;
            case FKeyAction.LaunchCalc when a.Down: Launch("calc.exe"); break;
            case FKeyAction.LaunchNotepad when a.Down: Launch("notepad.exe"); break;
            case FKeyAction.LaunchTaskMgr when a.Down: Launch("taskmgr.exe"); break;
            case FKeyAction.LaunchCustom when a.Down:
                var list = _settings.CustomLaunchers;
                if (a.CustomSlot < list.Count && !string.IsNullOrWhiteSpace(list[a.CustomSlot])) Launch(list[a.CustomSlot]);
                break;
        }
    }

    private static ushort MediaVk(FKeyAction a) => a switch
    {
        FKeyAction.PrevTrack => 0xB1,   // VK_MEDIA_PREV_TRACK
        FKeyAction.NextTrack => 0xB0,   // VK_MEDIA_NEXT_TRACK
        FKeyAction.PlayPause => 0xB3,   // VK_MEDIA_PLAY_PAUSE
        FKeyAction.Stop => 0xB2,        // VK_MEDIA_STOP
        FKeyAction.Mute => 0xAD,        // VK_VOLUME_MUTE
        FKeyAction.VolumeDown => 0xAE,  // VK_VOLUME_DOWN
        FKeyAction.VolumeUp => 0xAF,    // VK_VOLUME_UP
        _ => 0
    };

    // Start repeating on press, stop on release.
    private void Repeat(KeyAction a, Action tick)
    {
        lock (_gate)
        {
            if (_repeats.Remove(a.Action, out var old)) old.Dispose();
            if (a.Down) _repeats[a.Action] = new Timer(_ => tick(), null, RepeatDelayMs, RepeatRateMs);
        }
    }

    private static void Launch(string command)
    {
        try { Process.Start(new ProcessStartInfo(command) { UseShellExecute = true }); }
        catch { /* missing app: ignore */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var t in _repeats.Values) t.Dispose();
            _repeats.Clear();
        }
    }

    // --- interop -------------------------------------------------------------

    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;
    private const int WM_APPCOMMAND = 0x319, APPCOMMAND_MEDIA_FAST_FORWARD = 49, APPCOMMAND_MEDIA_REWIND = 50;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    // INPUT is a union sized for MOUSEINPUT; pad so Marshal.SizeOf matches the native struct.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

    private static void SendKey(ushort vk, bool down)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_EXTENDEDKEY | (down ? 0 : KEYEVENTF_KEYUP) }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void AppCommand(int cmd)
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd != IntPtr.Zero) SendMessage(hwnd, WM_APPCOMMAND, hwnd, (IntPtr)(cmd << 16));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);
}
