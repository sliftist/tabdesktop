Features

## Interleaved browser tabs in the strip
- Expanded browser tabs now stay grouped with their parent window and take that window's place in the strip's order, instead of all being pushed to the end after every real window — so a window with its tabs fanned out no longer buries the other windows behind it.
- Each tab and window carries its own persistent order key, and a window's expanded tabs additionally share a block key (the minimum key among that window's tabs). Ordering sorts primarily by block key, which keeps a window's tabs clumped together while still interleaving those blocks with plain windows at the right position.
- Because a tab's key no longer derives from a fixed offset off its parent, manually reordering a single tab moves it (and its window's block) to the drop position without disturbing the persisted keys of the surrounding entries.

## Sortable List tab
- The List tab's columns are now clickable: click a column header to sort by it, and click the same header again to flip between ascending and descending.
- A new Order column shows each entry's effective sort value, including the `min …` block key used for windows with expanded tabs, so the ordering the strip uses is visible directly in the list.
- The Order column sorts by its underlying numeric key rather than the decorated display text, so `min …` rows stay in numeric order instead of clustering apart from plain keys.

Bug Fixes

## Tab drag-and-drop landing in the wrong spot
- With browser-tab blocks interleaved among windows, the strip's display order is no longer monotonic in the underlying keys, since blocks sort by their minimum tab key.
- The old drop logic picked a new key from the immediate visual neighbors, so dropping a tab or window next to a block could compute a key that placed it somewhere other than where it was released.
- Dropping now derives the new key from the minimum key before the slot and the minimum key after it (falling back to zero when those keys run non-linear), guaranteeing the dropped entry — and its whole window block — lands at the drop position.
