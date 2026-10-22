using MagicKeyboard.Shared;

namespace MagicKeyboard.Gui;

// Small rolling log next to settings.json (%APPDATA%\MagicKeyboard\app.log): driver connects, disconnects
// and recoveries, so a problem like a stalled driver link can be diagnosed after the fact. Never throws.
internal static class AppLog
{
    private static readonly string LogPath =
        Path.Combine(Path.GetDirectoryName(KeyboardSettings.DefaultPath)!, "app.log");
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 256 * 1024)
                    File.Move(LogPath, LogPath + ".old", overwrite: true);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}
