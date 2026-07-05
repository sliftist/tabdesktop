using System.Windows.Media;

namespace TabDesktop;

// Fallback when a tab's exact URL has no cached thumbnail: on domains with enough cached history, the query-string parameter whose values vary the most across that domain's cached URLs is almost certainly the content identifier (YouTube's v=, Jellyfin's id=), so a cached URL sharing that parameter's value shows the same content even though the rest of the URL (timestamps, tracking params, playlist context) differs. This only runs after the live extension report and the exact disk lookup have both missed; a later extension report for the title replaces the fuzzy image with the real one.
public static class ThumbnailFuzzyMatch
{
    private const int MinDomainThumbnails = 10;
    // A value shared by this many cached URLs isn't a content identifier (think hl=en or ar=1) — matching on it would show some unrelated page's thumbnail.
    private const int MaxValueMatches = 20;

    public static ImageSource? TryLoad(string url)
    {
        try
        {
            return Resolve(url);
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(ThumbnailFuzzyMatch), ex.ToString());
            return null;
        }
    }

    private static ImageSource? Resolve(string url)
    {
        string? host = TabDomains.GetHost(url);
        if (host is null)
        {
            return null;
        }
        List<(string Key, string Value)> targetParams = ParseQuery(url);
        if (targetParams.Count == 0)
        {
            return null;
        }
        List<string> domainUrls = CollectCachedUrlsForHost(host, url);
        if (domainUrls.Count < MinDomainThumbnails)
        {
            return null;
        }
        var parsed = domainUrls.Select(u => (Url: u, Params: ParseQuery(u))).ToList();
        var occurrences = new Dictionary<string, int>();
        var distinctValues = new Dictionary<string, HashSet<string>>();
        foreach ((_, List<(string Key, string Value)> parameters) in parsed)
        {
            foreach ((string key, string value) in parameters)
            {
                occurrences[key] = occurrences.GetValueOrDefault(key) + 1;
                if (!distinctValues.TryGetValue(key, out HashSet<string>? values))
                {
                    values = new HashSet<string>();
                    distinctValues[key] = values;
                }
                values.Add(value);
            }
        }
        // Uniqueness = distinct values / occurrences: 1.0 means every cached URL carried a different value (an identifier), near 0 means a constant like hl=en. Ties break toward the better-attested key.
        List<(string Key, string Value)> ordered = targetParams
            .Where(p => occurrences.GetValueOrDefault(p.Key) > 0)
            .OrderByDescending(p => (double)distinctValues[p.Key].Count / occurrences[p.Key])
            .ThenByDescending(p => occurrences[p.Key])
            .ToList();
        foreach ((string key, string value) in ordered)
        {
            List<string> matches = parsed
                .Where(p => p.Params.Any(q => q.Key == key && q.Value == value))
                .Select(p => p.Url)
                .ToList();
            if (matches.Count == 0)
            {
                continue;
            }
            // Params are ordered most-unique first, so if even this one's value is too widely shared, everything after it is more generic still — give up rather than degrade.
            if (matches.Count > MaxValueMatches)
            {
                return null;
            }
            foreach (string candidateUrl in matches)
            {
                ImageSource? image = ThumbnailDiskCache.TryLoad(candidateUrl);
                if (image is not null)
                {
                    AppLog.Write(nameof(ThumbnailFuzzyMatch), $"No exact thumbnail for \"{url}\" — using \"{candidateUrl}\" matched on {key}={value}");
                    return image;
                }
            }
        }
        return null;
    }

    // Union of the disk cache's URL index and the live title→URL map, restricted to the host, deduped by hash, and verified to still have a file on disk. The target itself is excluded — its exact lookup already missed.
    private static List<string> CollectCachedUrlsForHost(string host, string targetUrl)
    {
        var seenHashes = new HashSet<string> { ThumbnailDiskCache.HashFor(targetUrl) };
        var result = new List<string>();
        IEnumerable<string> candidates = ThumbnailDiskCache.GetIndexedUrls()
            .Concat(ExtensionThumbnails.SnapshotSavedUrls().Select(pair => pair.Url).OfType<string>());
        foreach (string candidate in candidates)
        {
            if (TabDomains.GetHost(candidate) != host || !seenHashes.Add(ThumbnailDiskCache.HashFor(candidate)))
            {
                continue;
            }
            if (ThumbnailDiskCache.HasSaved(candidate))
            {
                result.Add(candidate);
            }
        }
        return result;
    }

    private static List<(string Key, string Value)> ParseQuery(string url)
    {
        var result = new List<(string Key, string Value)>();
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Query.Length == 0)
        {
            return result;
        }
        foreach (string part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            string key = eq < 0 ? part : part[..eq];
            string value = eq < 0 ? "" : part[(eq + 1)..];
            result.Add((Uri.UnescapeDataString(key), Uri.UnescapeDataString(value)));
        }
        return result;
    }
}
