# Desktop navigation memory

Date: 2026-10-12

## Problem and result

Previously every navigation pushed a complete `ContentPage` into Avalonia's
navigation stack. A route/playlist ViewModel was also cached for the lifetime of
the shell. Alternating Library and Search 100 times retained 101 pages and 6,635
visual descendants. Visiting another 30 playlists retained 131 pages and 32
ViewModels. Clearing forward history did not release those cached models.

Navigation now presents one page after each transition. Back/forward history is
limited to 20 entries containing a route, playlist ID, track-list state, and scroll
offset. Normal page models use an LRU cache of at most four inactive models.
Search stays cached to preserve results; Lyrics stays cached because the floating
lyrics window uses it. Editors and playlist matching have separate transient
models attached to their bounded history entries. An editor's unsaved fields and
inspection results survive back/forward within that history window. When an entry
is discarded, a running save or matching operation finishes before its model is
disposed. Dropping an entry beyond the 20-entry limit also ends the ability to
return to that editor draft.

Inactive track-list pages skip library and playback updates and refresh on
activation. The outgoing view's content and DataContext are released after the
transition. Deleted playlist entries are removed from history and cached models
are evicted. Shutdown serializes with navigation, detaches the current view, and
awaits page disposal.

## Headless measurements

The diagnostic under `artifacts/navigation-audit` uses the real Avalonia views,
isolated settings and no user library. Transitions are disabled for deterministic
counts. These are object counts, not managed or native heap measurements.

| Stage | Before: views | After: views | Before: ViewModels | After: ViewModels |
| --- | ---: | ---: | ---: | ---: |
| Initial library | 1 | 1 | 1 | 1 |
| 100 Library/Search switches | 101 | 1 | 2 | 2 |
| Then 30 playlists | 131 | 1 | 32 | 6 |
| Shutdown | 3 retained by host | 0 view contents | 0 owned | 0 owned |

The host retains one empty `ContentPage` object after shutdown, but its `Content`
and `DataContext` are null. The interactive tests also cover 1,000 route switches,
LRU eviction and restored selection/filter/scroll position, editor drafts,
forward-history removal, and deleted playlists.

The limit values are initial bounds, not measured optimal sizes. Profiling a
real library with large artwork and metadata results is still useful, especially
for native image memory and the maximum number of editor drafts in history.
