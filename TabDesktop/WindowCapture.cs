using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TabDesktop.Interop;

namespace TabDesktop;

public static class WindowCapture
{
    private const int ThumbnailMaxWidth = 640;

    // Runs on a per-process worker thread only: PrintWindow makes the target window paint itself, which blocks forever if its process is hung.
    public static BitmapSource? Capture(IntPtr hwnd, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        double scale = Math.Min(1.0, (double)ThumbnailMaxWidth / width);
        int thumbWidth = Math.Max(1, (int)(width * scale));
        int thumbHeight = Math.Max(1, (int)(height * scale));

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr fullDc = NativeMethods.CreateCompatibleDC(screenDc);
        IntPtr fullBitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
        IntPtr thumbDc = NativeMethods.CreateCompatibleDC(screenDc);
        IntPtr thumbBitmap = NativeMethods.CreateCompatibleBitmap(screenDc, thumbWidth, thumbHeight);
        try
        {
            NativeMethods.SelectObject(fullDc, fullBitmap);
            NativeMethods.SelectObject(thumbDc, thumbBitmap);
            // PW_RENDERFULLCONTENT is required for DWM-composed content (browsers, Electron, UWP) — without it those windows come back black.
            if (!NativeMethods.PrintWindow(hwnd, fullDc, NativeMethods.PW_RENDERFULLCONTENT))
            {
                return null;
            }
            NativeMethods.SetStretchBltMode(thumbDc, NativeMethods.HALFTONE);
            NativeMethods.StretchBlt(thumbDc, 0, 0, thumbWidth, thumbHeight, fullDc, 0, 0, width, height, NativeMethods.SRCCOPY);
            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(thumbBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // GDI blits leave the 32bpp alpha channel zeroed, and CreateBitmapSourceFromHBitmap surfaces that as Bgra32 — every pixel fully transparent, so the image renders as nothing. Re-typing to Bgr32 keeps the same pixel bytes but ignores alpha.
            var opaque = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
            opaque.Freeze();
            return opaque;
        }
        finally
        {
            NativeMethods.DeleteDC(fullDc);
            NativeMethods.DeleteObject(fullBitmap);
            NativeMethods.DeleteDC(thumbDc);
            NativeMethods.DeleteObject(thumbBitmap);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
