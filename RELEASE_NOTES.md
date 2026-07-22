Features

### Browser tab order follows the browser

- When a window's tabs are first shown after TabDesktop starts, their strip order now matches the tab order in the browser itself, so tabs you rearranged in Brave/Chrome while TabDesktop was closed appear in that same order instead of the last order TabDesktop remembered.
- The persisted per-tab name keys are re-seeded in the browser's reported index order the first time each window is seen per run, anchored at the block's existing minimum key so the whole block keeps its place relative to other windows and doesn't jump.
- In-session drags take over from there, so manual reordering within a running session still sticks.

### Block-start marker for tab runs

- Adds a black edge line on the first tab of each window's run, making it easy to see where one expanded browser window's tabs end and the next window's begin.
- Ordinary tabs keep the existing light edge line; only the leading tab of a window block (the first tab, or one whose window differs from the tab before it) is blackened.

### Strip sits inside its windows and squeezes gracefully

- The tab strip now starts slightly inset from the group's left edge rather than flush against (or past) it, so it reads as sitting on top of the windows it covers.
- Near the right edge of the screen a wide strip is trimmed in width rather than shifted inward, keeping its natural left position. It only slides left once the remaining room drops below a minimum width, and then only far enough to keep that minimum on-screen.

Bug Fixes

### Dragging a tab across window blocks landed in the wrong place

- Reorder a browser tab by dropping it next to a tab belonging to a *different* expanded window in the strip.
- Because tab keys aren't globally monotonic (blocks sort by their minimum key) and because plain windows could sit between tab blocks, the dropped tab's new key was computed against the wrong neighbors, so the tab (and its whole block) could land somewhere other than the drop position.
- Tab entries and plain-window entries are now treated as two separate orderings, so a plain window between two tab blocks can no longer corrupt a tab's placement, and a cross-window drop is anchored to the target window's block start (averaging that block's first tab with the entry before it) so the dragged tab lands ahead of the whole block as intended.
