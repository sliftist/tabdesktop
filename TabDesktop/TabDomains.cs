namespace TabDesktop;

// Maps a browser window to its URL's host so the thumbnail whitelist can key on domains. The extension report is the authoritative source (exact URL for the live tab); browser History is the fallback. Hosts cache per page title; failed lookups are not cached because an extension report or History checkpoint may supply the URL later.
public static class TabDomains
{
    private static readonly object gate = new();
    private static readonly Dictionary<string, string> hostByPageTitle = new();
    private static readonly HashSet<string> pending = new();

    public static string? GetHost(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Host.Length == 0)
        {
            return null;
        }
        string host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host["www.".Length..] : host;
    }

    // Synchronous (may query History sqlite); call from a background thread.
    public static string? Resolve(string windowTitle)
    {
        string? pageTitle = BrowserFavicon.GetPageTitle(windowTitle);
        if (pageTitle is null)
        {
            return null;
        }
        string key = BrowserFavicon.StripNotificationCount(pageTitle);
        lock (gate)
        {
            if (hostByPageTitle.TryGetValue(key, out string? cached))
            {
                return cached;
            }
        }
        string? url = ExtensionThumbnails.TryGetUrl(windowTitle) ?? BrowserFavicon.FindUrlForPageTitle(pageTitle);
        if (url is null && key != pageTitle)
        {
            url = BrowserFavicon.FindUrlForPageTitle(key);
        }
        string? host = GetHost(url);
        if (host is not null)
        {
            lock (gate)
            {
                hostByPageTitle[key] = host;
            }
        }
        return host;
    }

    // Cache-only lookup for bindings; kicks off a background resolve and announces via onResolved.
    public static string? TryGet(string windowTitle, Action onResolved)
    {
        string? pageTitle = BrowserFavicon.GetPageTitle(windowTitle);
        if (pageTitle is null)
        {
            return null;
        }
        string key = BrowserFavicon.StripNotificationCount(pageTitle);
        lock (gate)
        {
            if (hostByPageTitle.TryGetValue(key, out string? cached))
            {
                return cached;
            }
            if (!pending.Add(key))
            {
                return null;
            }
        }
        Task.Run(() =>
        {
            string? host = null;
            try
            {
                host = Resolve(windowTitle);
            }
            catch (Exception ex)
            {
                AppLog.Write(nameof(TabDomains), ex.ToString());
            }
            lock (gate)
            {
                pending.Remove(key);
            }
            if (host is not null)
            {
                onResolved();
            }
        });
        return null;
    }
}
