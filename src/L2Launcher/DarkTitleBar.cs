using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace L2Launcher;

/// <summary>
/// Dark native title bar. Win10 1809+/Win11: DWMWA_USE_IMMERSIVE_DARK_MODE (20; 19 on builds before 18985).
/// Win11 (22000+): also caption/text/border colors matching the theme. Any failure is ignored (light title bar).
/// </summary>
internal static class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static int ColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    public static void Apply(Window w)
    {
        try
        {
            var build = Environment.OSVersion.Version.Build;
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return; // before Win10 1809: not supported
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = 1;
            int attr = build >= 18985 ? DWMWA_USE_IMMERSIVE_DARK_MODE : DWMWA_USE_IMMERSIVE_DARK_MODE_OLD;
            if (DwmSetWindowAttribute(hwnd, attr, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, attr == DWMWA_USE_IMMERSIVE_DARK_MODE ? DWMWA_USE_IMMERSIVE_DARK_MODE_OLD : DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
            if (build >= 22000)
            {
                int caption = ColorRef(0x15, 0x17, 0x1C); // Bg
                int text = ColorRef(0xE6, 0xE6, 0xE6);    // Fg
                int border = ColorRef(0x3A, 0x40, 0x4C);
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
            }
        }
        catch { /* older Windows / no DWM: keep the default title bar */ }
    }
}
