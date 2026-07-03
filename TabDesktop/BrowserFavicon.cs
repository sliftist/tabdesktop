using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;

namespace TabDesktop;

// Recovers the active tab's favicon for Chrome/Brave/Edge windows without an extension: the window title gives the page title, the browser's History sqlite maps title → URL, and its Favicons sqlite maps URL → icon bytes. The browser holds mandatory byte-range locks on the live files, so a plain file copy throws IOException over the locked regions — instead we open the live files directly with SQLite's immutable=1, which bypasses locking and reads the main db (ignoring the WAL, so brand-new visits can lag a checkpoint). Everything runs on background tasks; resolution is announced via onResolved and served from cache afterwards.
public static class BrowserFavicon
{
    private static readonly Regex BrowserTitleRegex = new(@"^(.*) - (Brave|Google Chrome|Microsoft.? Edge)$");
    private static readonly Regex NotificationCountRegex = new(@"^\(\d+\)\s+");
    private const int PreferredIconSize = 32;

    private static readonly string[] ProfileDirs =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BraveSoftware", "Brave-Browser", "User Data", "Default"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data", "Default"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data", "Default"),
    };

    private static readonly object gate = new();
    private static readonly Dictionary<string, CachedIcon> resolvedByPageTitle = new();
    private static readonly HashSet<string> pending = new();
    // Misses retry quickly — the visit may simply not be checkpointed into History yet, or the site's favicon was just added (common during development). Hits refresh occasionally so a changed favicon shows up without an app restart.
    private static readonly TimeSpan MissRetryInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan HitRefreshInterval = TimeSpan.FromMinutes(5);

    private sealed record CachedIcon(ImageSource? Image, long ResolvedAt);

    // Sites surface an unread count in document.title ("(3) Inbox"); stripping it lets titles match History rows and extension reports regardless of when the count changed.
    public static string StripNotificationCount(string title)
    {
        return NotificationCountRegex.Replace(title, "");
    }

    // Extracts the page title from a browser window title, or null when the window isn't a recognized browser.
    public static string? GetPageTitle(string windowTitle)
    {
        Match match = BrowserTitleRegex.Match(windowTitle);
        return match.Success ? match.Groups[1].Value : null;
    }

    // The " - Brave"/" - Google Chrome" tail, for composing synthetic window titles for individual browser tabs.
    public static string? GetBrowserSuffix(string windowTitle)
    {
        Match match = BrowserTitleRegex.Match(windowTitle);
        return match.Success ? $" - {match.Groups[2].Value}" : null;
    }

    public static ImageSource? TryGet(string title, Action onResolved)
    {
        string? pageTitle = GetPageTitle(title);
        if (pageTitle is null)
        {
            return null;
        }
        ImageSource? staleImage = null;
        lock (gate)
        {
            if (resolvedByPageTitle.TryGetValue(pageTitle, out CachedIcon? cached))
            {
                TimeSpan age = Stopwatch.GetElapsedTime(cached.ResolvedAt);
                bool fresh = cached.Image is null ? age <= MissRetryInterval : age <= HitRefreshInterval;
                if (fresh)
                {
                    return cached.Image;
                }
                // Stale-while-revalidate: keep showing the old icon while the refresh runs.
                staleImage = cached.Image;
            }
            if (!pending.Add(pageTitle))
            {
                return staleImage;
            }
        }
        Task.Run(() =>
        {
            ImageSource? image = null;
            try
            {
                image = Resolve(pageTitle);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
            }
            bool changed;
            lock (gate)
            {
                ImageSource? previous = resolvedByPageTitle.TryGetValue(pageTitle, out CachedIcon? old) ? old.Image : null;
                // A failed refresh keeps the previous icon rather than flickering it away.
                ImageSource? effective = image ?? previous;
                resolvedByPageTitle[pageTitle] = new CachedIcon(effective, Stopwatch.GetTimestamp());
                pending.Remove(pageTitle);
                changed = !ReferenceEquals(effective, previous);
            }
            if (changed)
            {
                onResolved();
            }
        });
        return staleImage;
    }

    // Looks up the most recently visited URL whose page title matches, across all known browser profiles.
    public static string? FindUrlForPageTitle(string pageTitle)
    {
        foreach (string profile in ProfileDirs)
        {
            string historyPath = Path.Combine(profile, "History");
            if (!File.Exists(historyPath))
            {
                continue;
            }
            string? url = QueryUrlForTitle(historyPath, pageTitle);
            if (url is not null)
            {
                return url;
            }
        }
        return null;
    }

    private static ImageSource? Resolve(string pageTitle)
    {
        foreach (string profile in ProfileDirs)
        {
            string historyPath = Path.Combine(profile, "History");
            string faviconsPath = Path.Combine(profile, "Favicons");
            if (!File.Exists(historyPath) || !File.Exists(faviconsPath))
            {
                continue;
            }
            string? url = QueryUrlForTitle(historyPath, pageTitle);
            if (url is null)
            {
                continue;
            }
            byte[]? iconBytes = QueryFaviconBytes(faviconsPath, url);
            if (iconBytes is null)
            {
                continue;
            }
            return DecodeImage(iconBytes);
        }
        return null;
    }

    // immutable=1 promises SQLite the file won't change under it, so it skips the byte-range locking that would otherwise fail against the running browser. mode=ro keeps it read-only. The URI form is required for these query parameters; spaces (e.g. "User Data") must be percent-encoded.
    private static string ImmutableConnectionString(string dbPath)
    {
        string uriPath = dbPath.Replace('\\', '/').Replace(" ", "%20");
        return $"Data Source=file:{uriPath}?immutable=1&mode=ro";
    }

    private static string? QueryUrlForTitle(string historyDb, string pageTitle)
    {
        using var connection = new SqliteConnection(ImmutableConnectionString(historyDb));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT url FROM urls WHERE title = $title ORDER BY last_visit_time DESC LIMIT 1";
        command.Parameters.AddWithValue("$title", pageTitle);
        return command.ExecuteScalar() as string;
    }

    private static byte[]? QueryFaviconBytes(string faviconsDb, string url)
    {
        using var connection = new SqliteConnection(ImmutableConnectionString(faviconsDb));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT fb.image_data FROM favicon_bitmaps fb JOIN icon_mapping im ON im.icon_id = fb.icon_id WHERE im.page_url = $url ORDER BY ABS(fb.width - $size) LIMIT 1";
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$size", PreferredIconSize);
        return command.ExecuteScalar() as byte[];
    }

    internal static ImageSource DecodeImage(byte[] data)
    {
        using var stream = new MemoryStream(data);
        BitmapFrame frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }
}
