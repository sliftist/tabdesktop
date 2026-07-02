using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TabDesktop;

// Tiny on-disk thumbnail store keyed by URL, so tab thumbnails survive app restarts (constant during development) without waiting on the extension or re-fetching. Files are aggressively downscaled JPEGs named by URL hash; recency is tracked via file mtime (touched on every load) so pruning drops the least recently used.
public static class ThumbnailDiskCache
{
    private static readonly string CacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabDesktop", "thumbnails");
    private const int MaxEntries = 100_000;
    private const int StoredHeight = 96;
    private const int JpegQuality = 70;
    // Frame-grab sources re-report the same URL every cycle; skip rewrites while the stored copy is fresh.
    private static readonly TimeSpan RewriteMinAge = TimeSpan.FromHours(1);

    public static ImageSource? TryLoad(string url)
    {
        try
        {
            string path = PathFor(url);
            if (!File.Exists(path))
            {
                return null;
            }
            ImageSource image = BrowserFavicon.DecodeImage(File.ReadAllBytes(path));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return image;
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(ThumbnailDiskCache), ex.ToString());
            return null;
        }
    }

    public static void Save(string url, byte[] imageBytes)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            string path = PathFor(url);
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < RewriteMinAge)
            {
                return;
            }
            using var input = new MemoryStream(imageBytes);
            BitmapFrame frame = BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapSource source = frame;
            if (frame.PixelHeight > StoredHeight)
            {
                double scale = (double)StoredHeight / frame.PixelHeight;
                source = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            }
            var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using FileStream output = File.Create(path);
            encoder.Save(output);
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(ThumbnailDiskCache), ex.ToString());
        }
    }

    // Enumerating a full cache is slow, so pruning runs once per app start on a background task, never on a lookup path.
    public static void PruneInBackground()
    {
        Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(CacheDir))
                {
                    return;
                }
                FileInfo[] files = new DirectoryInfo(CacheDir).GetFiles("*.jpg");
                if (files.Length <= MaxEntries)
                {
                    return;
                }
                foreach (FileInfo file in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - MaxEntries))
                {
                    file.Delete();
                }
            }
            catch (Exception ex)
            {
                AppLog.Write(nameof(ThumbnailDiskCache), ex.ToString());
            }
        });
    }

    private static string PathFor(string url)
    {
        return Path.Combine(CacheDir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".jpg");
    }
}
