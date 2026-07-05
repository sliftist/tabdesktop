## Features

### Incognito browsing stays off disk

- Pages open in incognito windows are now kept out of TabDesktop's on-disk caches: their thumbnails are never written to the thumbnail cache, and their title/URL entries are excluded from the saved titles file.
- The browser extension now reports an incognito flag with every tab and page report, and a title that switches into incognito mode is scrubbed from the persisted title→URL file on the next save (and starts persisting again once it leaves incognito).
- Incognito tabs still get full in-memory treatment, so thumbnails and titles work normally while the tabs are open — nothing just survives a restart.

### Delete cached thumbnails from the Thumbnails tab

- Adds a "Delete matched" button to the Thumbnails tab that deletes every row matching the current search — the cached thumbnail files and their saved title/URL entries. With an empty search it deletes everything.
- Deletion applies to all matches of the search, not just the rows shown under the display cap, and runs without a confirmation prompt since the cache is regenerable.
- Titles belonging to still-open tabs are re-learned on the next report, so deleting clears history without breaking live thumbnails.

## Bug Fixes

### Fuzzy thumbnail matching could show an unrelated page's thumbnail

- Visiting a page whose URL shares a generic query-parameter value (like `hl=en` or `ar=1`) with many other cached pages could fuzzy-match against any of them.
- The tab strip would show a thumbnail from a completely unrelated page.
- Fuzzy matching now treats a parameter value shared by more than 20 cached URLs as too generic to identify content, and gives up rather than match on it — since parameters are checked most-unique first, anything after it would be even less specific.
