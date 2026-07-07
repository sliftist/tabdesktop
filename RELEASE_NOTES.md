Bug Fixes

### Tab order no longer shuffles when you focus a window

- Previously, when two windows in a group shared the same sort position, TabDesktop broke the tie by z-order — so clicking a tab (which raises its window) changed the tie-break and the tabs visibly reshuffled every time you switched between them.
- The result was a strip whose tab order was never stable: the act of using a tab moved it, making it hard to build muscle memory for where a window sits.
- Ties are now resolved by process id, then window handle for windows sharing a process — deterministic values that don't change on focus — so a tab keeps its slot no matter how often you click it. Expanded browser tabs still sort after every real window.

### Tab order stays consistent across restarts and for windows that share a name

- When TabDesktop first saw a new window it assigned it an order from an ever-incrementing counter seeded off the last-used value, so the position depended on when the app happened to notice the window rather than anything intrinsic — restarts and window recreation could land a window in a different spot.
- Apps that expose several windows under one title (which share a single persisted order key) had no stable way to separate, and windows still showing the "loading" placeholder were all pinned to position 0.
- New windows now default to their process creation time (whole seconds) for a position that's stable regardless of when TabDesktop starts, placeholder windows fall back to that same creation-time ordering instead of collapsing to 0, and same-second collisions are cleanly separated by the process-id tie-break. Elevated processes whose creation time can't be read fall back to first-seen time.
