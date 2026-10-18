using System.Runtime.InteropServices;

namespace MagicKeyboard.Gui;

internal static class NativeUi
{
    public static readonly int ShowMessage = RegisterWindowMessage("MagicKeyboard.Show");
    private const int HWND_BROADCAST = 0xFFFF;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

    public static void PostShowMessage() => PostMessage((IntPtr)HWND_BROADCAST, ShowMessage, IntPtr.Zero, IntPtr.Zero);
}
