## Features

### Thumbnails browser tab
- Adds a **Thumbnails** tab to the main window that lists every cached thumbnail with its title, URL, last-used date, file size, and file hash, sorted most recently used first.
- Search filters instantly against every field of a row with case-insensitive contains matching; only the first 200 matches render (and decode their images) at a time, keeping the tab responsive against a 100k-row cache.
- The list is a snapshot built off the UI thread; an "Out of date" indicator appears when thumbnails or titles change underneath it, and a new search or the Refresh button rebuilds it.

### Smarter thumbnail matching
- Shows a thumbnail for pages whose exact URL was never cached: when a domain has enough cached history, the query parameter whose values vary the most across that domain is treated as the content identifier (YouTube's `v=`, Jellyfin's `id=`), and a cached URL sharing that value stands in until the real thumbnail arrives.
- Persists a URL index alongside the thumbnail cache so cached files keep a recoverable URL across restarts; existing files backfill automatically as their URLs keep reporting.
- Learns title→URL mappings from browser tab events, which arrive well before the 15-second thumbnail cycle, so a freshly loaded page resolves its cached thumbnail immediately instead of showing a favicon first.
- Raises the remembered title→URL cache from 500 to 20,000 entries.

### Instant tab switching
- Clicking a tab in the strip highlights it and updates focus state immediately instead of waiting for the next window poll or the browser's confirmation report; the regular polling then converges to the actual state if the switch failed.
- Applies to both native windows and expanded browser-tab entries — the clicked browser tab's active highlight moves optimistically before the extension confirms.

## Bug Fixes

### Tab strips could slip behind other windows
- Repro: open an app that makes itself topmost, or go through certain fullscreen transitions, with a tab strip visible.
- Consequence: the strip ended up covered or lost its always-on-top status entirely.
- Fix: re-asserts the strip's topmost position on every refresh via `SetWindowPos`, without activating it, so it never steals focus doing so.

### Tab strip clicks lost during refreshes
- Repro: press a tab in the strip right as a background refresh replaced the strip's contents.
- Consequence: the hover overlay flashed and the click was eaten because the pressed tab's container was recycled before the mouse release.
- Fix: syncs the strip's tab list in place — removing, inserting, and moving individual entries — so unchanged tabs keep their containers alive across refreshes.

### Thumbnails flashed back to favicons on page load
- Repro: navigate a browser tab so its window title changes (e.g. starting a new video).
- Consequence: the tile dropped to the favicon for several seconds while every thumbnail source re-resolved against the new title, even though a thumbnail was about to arrive.
- Fix: keeps showing the previous thumbnail for up to 20 seconds after a title change while sources resolve; genuinely thumbnail-less pages still fall back to their icon afterwards.

### Favicons could stop resolving until restart
- Repro: TabDesktop reads the browser's history/favicon database at the moment the running browser writes to it.
- Consequence: SQLite reported the database as malformed, and a pooled connection kept returning that error for every later favicon lookup until the app restarted.
- Fix: treats a torn read of the live browser database as a cache miss that retries on the next cycle, and disables connection pooling so a bad handle is never reused.
