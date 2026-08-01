using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OrdoSort.Wpf.Services;

/// <summary>Dark title bar via DWMWA_USE_IMMERSIVE_DARK_MODE (attr 20,
/// Win10 1903+/Win11). Failure is cosmetic — swallow it.</summary>
public static class TitleBarChrome
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr,
        ref int value, int size);

    public static void ApplyDarkTitleBar(Window window, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            var v = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int));
        }
        catch { /* cosmetic only */ }
    }
}
