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

    // Bumped on every write or prune so the Thumbnails tab can tell its snapshot is out of date. Loads touch mtime but don't count — treating a read as a change would leave the tab perpetually stale.
    private static int version;
    public static int Version => Volatile.Read(ref version);

    // Files are named by URL hash, so without a side index the URLs of cached thumbnails would be unrecoverable — and both the fuzzy same-domain matcher and the Thumbnails tab need them. Plain one-URL-per-line append; URLs can't contain newlines.
    private static readonly string UrlIndexPath = Path.Combine(CacheDir, "url-index.txt");
    private static readonly object indexGate = new();
    private static Dictionary<string, string>? urlByHash;

    private static Dictionary<string, string> EnsureIndex()
    {
        lock (indexGate)
        {
            if (urlByHash is null)
            {
                urlByHash = new Dictionary<string, string>();
                try
                {
                    if (File.Exists(UrlIndexPath))
                    {
                        foreach (string line in File.ReadLines(UrlIndexPath))
                        {
                            if (line.Length > 0)
                            {
                                urlByHash[HashFor(line)] = line;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write(nameof(ThumbnailDiskCache), ex.ToString());
                }
            }
            return urlByHash;
        }
    }

    private static void RecordUrl(string url)
    {
        lock (indexGate)
        {
            Dictionary<string, string> index = EnsureIndex();
            string hash = HashFor(url);
            if (index.ContainsKey(hash))
            {
                return;
            }
            index[hash] = url;
            File.AppendAllText(UrlIndexPath, url + Environment.NewLine);
        }
    }

    public static string? TryGetUrlForHash(string hash)
    {
        lock (indexGate)
        {
            return EnsureIndex().GetValueOrDefault(hash);
        }
    }

    // May include URLs whose file has since been pruned — callers verify with HasSaved/TryLoad.
    public static List<string> GetIndexedUrls()
    {
        lock (indexGate)
        {
            return EnsureIndex().Values.ToList();
        }
    }

    public static bool HasSaved(string url)
    {
        return File.Exists(PathFor(url));
    }

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
            // Recorded even when the rewrite is skipped, so files saved before the index existed backfill as their URLs keep reporting.
            RecordUrl(url);
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
            using (FileStream output = File.Create(path))
            {
                encoder.Save(output);
            }
            Interlocked.Increment(ref version);
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
                Interlocked.Increment(ref version);
                CompactIndex();
            }
            catch (Exception ex)
            {
                AppLog.Write(nameof(ThumbnailDiskCache), ex.ToString());
            }
        });
    }

    public sealed record SavedThumbnail(string Hash, string Path, DateTime LastUsedUtc, long SizeBytes);

    // Everything on disk, for the Thumbnails browse tab. mtime doubles as last-used (touched on every load), so it's surfaced under that name rather than as a save date.
    public static List<SavedThumbnail> ListSaved()
    {
        try
        {
            if (!Directory.Exists(CacheDir))
            {
                return new List<SavedThumbnail>();
            }
            return new DirectoryInfo(CacheDir).GetFiles("*.jpg")
                .Select(f => new SavedThumbnail(Path.GetFileNameWithoutExtension(f.Name), f.FullName, f.LastWriteTimeUtc, f.Length))
                .ToList();
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(ThumbnailDiskCache), ex.ToString());
            return new List<SavedThumbnail>();
        }
    }

    // Backs the Thumbnails tab's delete button.
    public static void DeleteMany(IEnumerable<string> hashes)
    {
        try
        {
            int deleted = 0;
            foreach (string hash in hashes)
            {
                string path = Path.Combine(CacheDir, hash + ".jpg");
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            if (deleted > 0)
            {
                Interlocked.Increment(ref version);
                CompactIndex();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(ThumbnailDiskCache), ex.ToString());
        }
    }

    // Deleting files (pruning, the Thumbnails tab's delete) is the only thing that shrinks the cache, so the index only needs rewriting from those paths to stay bounded by MaxEntries.
    private static void CompactIndex()
    {
        lock (indexGate)
        {
            Dictionary<string, string> kept = EnsureIndex()
                .Where(pair => File.Exists(Path.Combine(CacheDir, pair.Key + ".jpg")))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            urlByHash = kept;
            File.WriteAllLines(UrlIndexPath, kept.Values);
        }
    }

    public static string HashFor(string url)
    {
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)));
    }

    private static string PathFor(string url)
    {
        return Path.Combine(CacheDir, HashFor(url) + ".jpg");
    }
}
