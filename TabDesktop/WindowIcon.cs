using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TabDesktop.Interop;

namespace TabDesktop;

// Last-resort tab icon: whatever icon Windows itself would show for the window — WM_GETICON first (it reflects per-window icons like a document editor's file icon), then the window-class icon, then the icon embedded in the process executable. WM_GETICON round-trips through the target window's message loop, which can block on a busy (or hung, despite SMTO_ABORTIFHUNG's best effort) app, so resolution runs on a background task and is announced via the onResolved callback, mirroring BrowserFavicon.
public static class WindowIcon
{
    private const uint GetIconTimeoutMs = 200;

    private static readonly object gate = new();
    private static readonly Dictionary<IntPtr, ImageSource?> resolvedByHwnd = new();
    private static readonly HashSet<IntPtr> pending = new();
    private static readonly Dictionary<string, ImageSource?> exeIconByPath = new();

    public static ImageSource? TryGet(IntPtr hwnd, uint pid, Action onResolved)
    {
        lock (gate)
        {
            if (resolvedByHwnd.TryGetValue(hwnd, out ImageSource? cached))
            {
                return cached;
            }
            if (!pending.Add(hwnd))
            {
                return null;
            }
        }
        Task.Run(() =>
        {
            ImageSource? image = null;
            try
            {
                image = Resolve(hwnd, pid);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
            }
            lock (gate)
            {
                resolvedByHwnd[hwnd] = image;
                pending.Remove(hwnd);
            }
            if (image is not null)
            {
                onResolved();
            }
        });
        return null;
    }

    private static ImageSource? Resolve(IntPtr hwnd, uint pid)
    {
        // ICON_BIG first: at high DPI a 32px icon downscaled to the 16px tab slot looks better than the 16px original upscaled by the display transform.
        foreach (int kind in new[] { NativeMethods.ICON_BIG, NativeMethods.ICON_SMALL2, NativeMethods.ICON_SMALL })
        {
            if (NativeMethods.SendMessageTimeout(hwnd, NativeMethods.WM_GETICON, new IntPtr(kind), IntPtr.Zero, NativeMethods.SMTO_ABORTIFHUNG, GetIconTimeoutMs, out IntPtr icon) != IntPtr.Zero && icon != IntPtr.Zero)
            {
                return FromHicon(icon);
            }
        }
        foreach (int index in new[] { NativeMethods.GCLP_HICON, NativeMethods.GCLP_HICONSM })
        {
            IntPtr icon = NativeMethods.GetClassLongPtr(hwnd, index);
            if (icon != IntPtr.Zero)
            {
                return FromHicon(icon);
            }
        }
        return FromProcessExe(pid);
    }

    private static ImageSource? FromProcessExe(uint pid)
    {
        string? exePath = GetProcessPath(pid);
        if (exePath is null)
        {
            return null;
        }
        lock (gate)
        {
            if (exeIconByPath.TryGetValue(exePath, out ImageSource? cached))
            {
                return cached;
            }
        }
        ImageSource? image = null;
        var largeIcons = new IntPtr[1];
        if (NativeMethods.ExtractIconEx(exePath, 0, largeIcons, null, 1) > 0 && largeIcons[0] != IntPtr.Zero)
        {
            try
            {
                image = FromHicon(largeIcons[0]);
            }
            finally
            {
                NativeMethods.DestroyIcon(largeIcons[0]);
            }
        }
        lock (gate)
        {
            exeIconByPath[exePath] = image;
        }
        return image;
    }

    private static string? GetProcessPath(uint pid)
    {
        IntPtr process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    // CreateBitmapSourceFromHIcon copies the pixels immediately, so HICONs owned by the target window/class are safe to convert without CopyIcon.
    private static ImageSource FromHicon(IntPtr hicon)
    {
        BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }
}
