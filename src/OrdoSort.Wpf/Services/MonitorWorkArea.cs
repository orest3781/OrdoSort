using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace OrdoSort.Wpf.Services;

/// <summary>The usable area — the screen minus the taskbar and any other
/// appbars — of the monitor a window is ACTUALLY on, in DIPs.
///
/// Why this exists: <see cref="SystemParameters.WorkArea"/> is the PRIMARY
/// monitor's work area and only ever that, which makes it a trap for any
/// code that uses it to keep a window on screen. The 2026-08-28 review found
/// <c>MainWindow.FitViewerTo</c> doing exactly that: a window the user had
/// placed on a right-hand secondary monitor (Left 2200, width 1280) was
/// pulled back to Left 640 at every session start, because 2200 + 1280 is
/// past the primary's Right of 1920. Resolving the window's own monitor is
/// what makes "stay inside the work area" mean the screen it is on.
///
/// Units: Win32 reports monitor rectangles in physical device pixels, while
/// WPF sizes and positions windows in DIPs. Those coincide only at 100%
/// scaling, so the rectangle is divided by the WINDOW's DPI scale — the
/// window's, not the system's, because under per-monitor DPI a window on a
/// 150% secondary has a different scale from the 100% primary, and
/// <see cref="VisualTreeHelper.GetDpi"/> is the one that follows the
/// window.</summary>
internal static class MonitorWorkArea
{
    /// <summary>The work area of the monitor nearest <paramref name="window"/>,
    /// in DIPs. Falls back to <see cref="SystemParameters.WorkArea"/> — the
    /// primary's, which is what every caller used before this class existed —
    /// when the window has no HWND yet (never shown) or Win32 declines to
    /// answer, so a caller can treat the result as always usable.</summary>
    public static Rect For(Window window)
    {
        // MonitorFromWindow needs a real HWND; a Window that was constructed
        // but never shown does not have one yet.
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return SystemParameters.WorkArea;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return SystemParameters.WorkArea;

        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info)) return SystemParameters.WorkArea;

        var dpi = VisualTreeHelper.GetDpi(window);
        var work = info.rcWork;
        return ToDips(work.Left, work.Top, work.Right, work.Bottom, dpi.DpiScaleX, dpi.DpiScaleY)
            ?? SystemParameters.WorkArea;
    }

    /// <summary>A monitor rectangle in device pixels as a WPF rectangle in
    /// DIPs; null when the numbers cannot make a usable one (a nonsense DPI
    /// scale, an empty or inverted rectangle), which is the caller's cue to
    /// fall back.
    ///
    /// Split out from <see cref="For"/> for the reason <see cref="FitMath"/>
    /// is split out of MainWindow: the P/Invoke half cannot be unit-tested,
    /// and this is the half with the arithmetic in it — including the part
    /// the primary-monitor trap actually turns on, that a monitor whose
    /// origin is not 0 (one to the right of the primary, or to the left with
    /// a negative Left) keeps that origin instead of being folded onto
    /// zero.</summary>
    internal static Rect? ToDips(int left, int top, int right, int bottom, double scaleX, double scaleY)
    {
        if (scaleX <= 0 || scaleY <= 0) return null;
        var width = (right - left) / scaleX;
        var height = (bottom - top) / scaleY;
        // Rect's constructor throws on a negative size, and this class
        // promises a usable rectangle rather than an exception.
        if (width <= 0 || height <= 0) return null;
        return new Rect(left / scaleX, top / scaleY, width, height);
    }

    /// <summary>MONITOR_DEFAULTTONEAREST: a window dragged (or placed by a
    /// test) partly or wholly off every screen still resolves to the closest
    /// monitor rather than to nothing.</summary>
    private const int MonitorDefaultToNearest = 0x00000002;

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    /// <summary>MONITORINFO. <c>cbSize</c> has to be filled in before the
    /// call — it is how the API tells this struct from the longer
    /// MONITORINFOEX — and the field order is the native contract, so
    /// nothing here may be reordered or dropped even though only
    /// <c>rcWork</c> is read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public int dwFlags;
    }
}
