namespace TabDesktop;

// Ranks tabs for the search popup: a full-title match beats a prefix match beats a substring match, ties broken by shorter title then most recently focused. Case-insensitive throughout.
public static class TabSearch
{
    public static List<WindowEntry> Rank(IEnumerable<WindowEntry> entries, string query, int maxResults)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return entries.OrderByDescending(e => e.LastFocusedAt).Take(maxResults).ToList();
        }
        return entries
            .Select(e => (Entry: e, Score: Score(e.DisplayTitle, query)))
            .Where(x => x.Score >= 0)
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Entry.DisplayTitle.Length)
            .ThenByDescending(x => x.Entry.LastFocusedAt)
            .Select(x => x.Entry)
            .Take(maxResults)
            .ToList();
    }

    private static int Score(string title, string query)
    {
        if (title.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (title.Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
        return -1;
    }
}
