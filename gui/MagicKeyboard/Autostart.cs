using Microsoft.Win32;

namespace MagicKeyboard.Gui;

// Per-user "start with Windows" via the Run key. No admin required.

internal static class Autostart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "MagicKeyboard";
    private const string OldName = "MagicKeyboardReborn";   // earlier builds; removed so it can't launch twice

    public static void Apply(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(Key, writable: true);
            if (run == null) return;
            run.DeleteValue(OldName, throwOnMissingValue: false);
            if (enabled)
                run.SetValue(Name, $"\"{Environment.ProcessPath}\" --minimized");   // same flag as MagicMouse
            else
                run.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch { /* non-fatal */ }
    }
}
