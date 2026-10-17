using System.Drawing;
using System.Drawing.Drawing2D;
using WinForms = System.Windows.Forms;

namespace MagicKeyboard.Gui;

// The MagicKeyboard icon embedded in the exe (Assets\MagicKeyboard.ico): grayed out when disabled, with a
// battery bar along the right edge like MagicMouse's tray icon.
internal static class TrayIcons
{
    public static Icon Get(int size)
    {
        using var s = typeof(TrayIcons).Assembly.GetManifestResourceStream("MagicKeyboard.ico")!;
        return new Icon(s, size, size);
    }

    public static Icon Build(bool enabled, int? batteryPercent)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var app = Get(32);
            if (enabled) g.DrawIcon(app, new Rectangle(0, 0, 32, 32));
            else
            {
                using var src = app.ToBitmap();
                WinForms.ControlPaint.DrawImageDisabled(g, src, 0, 0, Color.Transparent);
            }
            if (batteryPercent is int p)
            {
                // Battery bar along the right edge, colored by level.
                var c = p <= 10 ? Color.Red : p <= 25 ? Color.Orange : Color.LimeGreen;
                using var back = new SolidBrush(Color.FromArgb(200, 30, 30, 30));
                using var bar = new SolidBrush(c);
                g.FillRectangle(back, 25, 3, 6, 26);
                int h = Math.Max(2, 24 * Math.Clamp(p, 0, 100) / 100);
                g.FillRectangle(bar, 26, 28 - h, 4, h);
            }
        }
        IntPtr hicon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hicon).Clone(); }
        finally { DestroyIcon(hicon); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
