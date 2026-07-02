using System.Text;
using System.Windows.Media.Imaging;
using TabDesktop.Interop;

namespace TabDesktop;

public sealed record WindowData(IntPtr Hwnd, string Title, int Left, int Top, int Width, int Height, bool IsMinimized, BitmapSource? Thumbnail);

public static class WindowScanner
{
    // "Maybe visible" = could plausibly appear on a monitor or in alt-tab: visible per user32, not a tool window, not DWM-cloaked (cloaked covers other virtual desktops and suspended UWP shells), and titled — untitled visible top-levels are almost always invisible helper hosts. Everything used here reads cached/user32-side state and cannot block on the target process, so it's safe outside the worker pool.
    public static List<(IntPtr Hwnd, uint Pid, bool IsMinimized)> FindCandidateWindows()
    {
        var result = new List<(IntPtr, uint, bool)>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (NativeMethods.GetWindowTextLength(hwnd) == 0) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            result.Add((hwnd, pid, NativeMethods.IsIconic(hwnd)));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    // Title (user32's cached caption), rect, and minimized state never wait on the target process, so this is safe on any thread — it's the whole poll for non-visible windows, which mostly just need "is it visible yet".
    public static WindowData? GatherBasics(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd)) return null;
        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect)) return null;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        int titleLength = NativeMethods.GetWindowTextLength(hwnd);
        var titleBuffer = new StringBuilder(titleLength + 1);
        NativeMethods.GetWindowText(hwnd, titleBuffer, titleBuffer.Capacity);
        bool minimized = NativeMethods.IsIconic(hwnd);
        return new WindowData(hwnd, titleBuffer.ToString(), rect.Left, rect.Top, width, height, minimized, null);
    }

    // Must run on the target process's worker thread — the capture can block forever on a hung process. Previews are only captured on request (foreground window, stale cache) rather than on every poll.
    public static WindowData? GatherOnWorkerThread(IntPtr hwnd, bool withPreview)
    {
        WindowData? basics = GatherBasics(hwnd);
        if (basics is null || basics.IsMinimized || !withPreview)
        {
            return basics;
        }
        return basics with { Thumbnail = WindowCapture.Capture(hwnd, basics.Width, basics.Height) };
    }
}
