using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace TabDesktop;

// Resolves the video thumbnail for YouTube tabs: the window title gives the page title, the browser History sqlite (via BrowserFavicon) maps it to the watch URL, and img.youtube.com serves the thumbnail by video id. Downloading a single thumbnail jpeg per video is the same lightweight request every search scraper makes, so no YouTube session or API key is needed. Resolution runs on background tasks, is announced via onResolved, and is served from cache afterwards (failures cache as null so we never hammer the endpoint).
public static class YouTubeThumbnail
{
    private const string PageTitleSuffix = " - YouTube";
    // mqdefault is the smallest variant that is true 16:9 with no letterboxing (hqdefault pads 4:3 with black bars).
    private const string ThumbnailUrlFormat = "https://img.youtube.com/vi/{0}/mqdefault.jpg";
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(10);

    // watch?v=, youtu.be/, shorts/, and live/ URLs all carry the 11-char video id.
    private static readonly Regex VideoIdRegex = new(@"(?:[?&]v=|youtu\.be/|/shorts/|/live/)([A-Za-z0-9_-]{11})");

    private static readonly HttpClient http = new() { Timeout = DownloadTimeout };
    private static readonly object gate = new();
    private static readonly Dictionary<string, ImageSource?> resolvedByPageTitle = new();
    private static readonly HashSet<string> pending = new();

    private const string YouTubeHost = "youtube.com";

    public static ImageSource? TryGet(string title, Action onResolved)
    {
        if (!ThumbnailWhitelist.IsDomainWhitelisted(YouTubeHost))
        {
            return null;
        }
        string? pageTitle = BrowserFavicon.GetPageTitle(title);
        if (pageTitle is null || !pageTitle.EndsWith(PageTitleSuffix, StringComparison.Ordinal))
        {
            return null;
        }
        lock (gate)
        {
            if (resolvedByPageTitle.TryGetValue(pageTitle, out ImageSource? cached))
            {
                return cached;
            }
            if (!pending.Add(pageTitle))
            {
                return null;
            }
        }
        string key = pageTitle;
        Task.Run(() =>
        {
            ImageSource? image = null;
            try
            {
                image = Resolve(key);
            }
            catch (Exception ex)
            {
                AppLog.Write(nameof(YouTubeThumbnail), $"Resolve failed for \"{key}\": {ex}");
            }
            lock (gate)
            {
                resolvedByPageTitle[key] = image;
                pending.Remove(key);
            }
            if (image is not null)
            {
                onResolved();
            }
        });
        return null;
    }

    private static ImageSource? Resolve(string pageTitle)
    {
        string? url = BrowserFavicon.FindUrlForPageTitle(pageTitle);
        if (url is null)
        {
            // History may hold the title with or without the unread-count prefix, so try both.
            string stripped = BrowserFavicon.StripNotificationCount(pageTitle);
            if (stripped != pageTitle)
            {
                url = BrowserFavicon.FindUrlForPageTitle(stripped);
            }
        }
        if (url is null)
        {
            AppLog.Write(nameof(YouTubeThumbnail), $"No browser History URL found for page title \"{pageTitle}\" — likely an un-checkpointed WAL entry or a profile outside the known dirs.");
            return null;
        }
        Match match = VideoIdRegex.Match(url);
        if (!match.Success)
        {
            AppLog.Write(nameof(YouTubeThumbnail), $"History URL for \"{pageTitle}\" has no video id: {url}");
            return null;
        }
        string thumbnailUrl = string.Format(ThumbnailUrlFormat, match.Groups[1].Value);
        ImageSource? cached = ThumbnailDiskCache.TryLoad(thumbnailUrl);
        if (cached is not null)
        {
            return cached;
        }
        byte[] bytes = http.GetByteArrayAsync(thumbnailUrl).GetAwaiter().GetResult();
        ThumbnailDiskCache.Save(thumbnailUrl, bytes);
        return BrowserFavicon.DecodeImage(bytes);
    }
}
