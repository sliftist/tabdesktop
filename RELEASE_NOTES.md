Bug Fixes

### Tab strip stays on screen when a group is dragged past an edge

- Dragging a window group toward the left, right, or bottom edge of the screen — or moving it so the strip would sit above the top of the display — used to push the tab strip partly or fully off-screen, leaving its tabs unreachable.
- Because the strip's position was set straight from the group's coordinates with no bound on the right and bottom edges, a group nudged past a boundary took its strip out of view along with it.
- Changes the strip to clamp its full rectangle into the virtual screen after its size is computed, so it slides to rest against the boundary and stays entirely visible no matter where the group is dragged.
