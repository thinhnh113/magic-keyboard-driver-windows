namespace MagicKeyboard.Gui;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\MagicKeyboard.SingleInstance", out bool first);
        if (!first)
        {
            // Ask the running instance to show its window.
            NativeUi.PostShowMessage();
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "MagicKeyboard - error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        // --minimized (the installer's and MagicMouse's flag); --autostart from earlier builds.
        bool minimized = args.Contains("--minimized") || args.Contains("--autostart");
        Application.Run(new TrayContext(minimized));
    }
}
