# LemonNeo desktop

The Avalonia UI 12 desktop host targets .NET 10 and shares the console's library,
playback, cache, configuration, and plugin contracts. MVVM uses
[CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/);
localization uses [Irihi.Lingua](https://github.com/irihitech/Irihi.Lingua).

```sh
git submodule update --init --recursive
dotnet run --project src/LemonNeo.Desktop
```

The build places default plugins beside the desktop executable in `plugins/`.
The host discovers their manifests dynamically. `--plugins <directory>` and
`--library <directory>` override those locations. `--startup <file>` selects the
shared Console startup script edited in Tools. The `LEMONNEO_*` environment
variables described in the root README also apply to the desktop host.

## UI infrastructure

`NavigationPage` hosts a stack of pages. Back/forward buttons and Alt+Left/Right
restore the existing page view, including selections, filters, tabs and drafts.
Navigating to a new destination clears forward history. Views and view models
are created on demand; individual playlists and the queue have separate state.
Each view owns its page header, actions, progress and error notices. The shell
owns startup, the sidebar and a separate playback view model.

The sidebar spans the full window height. The right column contains a compact
top toolbar, the active page and the playback bar at its bottom. Back/forward
buttons use 32-pixel targets in the toolbar alongside global search. Enter or
the search button opens the search page on demand and starts its own operation;
the results page does not duplicate the search input. The playback bar centers
previous/play-pause/next in the right column, using equal side regions. Clicking
the cover, title or artist opens Now playing. Playback mode uses an icon with
a localized tooltip. A full-width thin seek slider sits on the content/playback
boundary; its thumb appears on hover or keyboard focus, and the loading indicator
overlays the same line. Time appears at the right. The bar has no stop or cancel
buttons. Playback glyphs are adapted from LemonLite's `Styles/IconData.xaml`.

Library and playlist playback always use automatic source selection. Their
filter input, track details and action buttons share one horizontal toolbar;
the filter expands to fill the remaining width.

The queue button at the right of the playback bar opens a full-height card at
the window's right edge. Its view model is created on first use, independently
of page navigation. The overlay popup slides in with `CubicEaseOut` and closes
when clicking outside, pressing Escape or using its close button. Outside
clicks are consumed so they do not activate the page beneath it. Reduced motion
opens the card immediately. Search filters the queue independently of library
filters; the current row is marked by queue item UUID so duplicate songs remain
distinct. Clicking a row or pressing Enter plays that existing queue item and
retains the chosen playback source. The card also provides Clear queue.

`PageViewModel` provides an operation lifetime, busy state, cancellation and
localized notices for each page. Search, online services, track inspection,
tools and settings operate independently. Slow provider calls run off the UI
thread, and completing a request on a hidden page does not change the current
destination. Abandoned editor pages cancel their work and unsubscribe events.
Folder scans, remote playlist reads and sync fetches are separated from applying
their results: provider work runs on a worker and library changes resume on the
UI thread, using the same application services as Console.

Navigation uses a 220 ms `SlideFadeTransition`: the old page hides immediately,
and the new page rises 32 pixels and fades in with `CubicEaseOut`.
Going back reverses the new page's travel direction. Transitions restore
transforms and opacity even after cancellation, and rapid navigation requests
are queued. Reduced motion disables page animations for subsequent navigation.

The default theme follows the operating system. Settings can select light, dark,
or system, independently of the language preference. Theme resources use
Avalonia `ThemeDictionaries` and `DynamicResource`, so existing windows update
when the theme changes. Returning to system mode clears the override and lets
Avalonia observe subsequent platform theme changes.

`Resources/Themes/Palette.axaml` owns semantic brush roles and Fluent accents;
`Tokens.axaml` owns typography, spacing, radii, and transition durations.
`Styles.axaml` supplies common component variants. `Controls.axaml` supplies the
button, page-header, empty-state, and artwork templates. `Templates.axaml` contains
shared track, provider, playlist, metadata-match, and source row templates. Views refer to these resources
instead of embedding color literals. The Components page previews shared
buttons, inputs, selectors, cards, and templates.

`AppearanceService` persists theme, language, and reduced-motion preferences in
the shared configuration store under `desktop.ui`, preserving other settings.
Writes succeed before live preferences change. Reduced motion sets the custom
template transition durations to zero. Fluent's own controls retain their
standard interaction behavior.

`Resources/Languages/Strings.json` is the invariant English catalog;
`Strings.zh-CN.json` is Simplified Chinese. Lingua's source generator checks
translation keys and generates `LanguageManager` and its strongly typed `Keys`.
Use `{Translate {x:Static i18n:LanguageManager+Keys.SomeKey}}` in XAML. Lingua
updates active bindings when the culture changes and supplies fallback text.
`LocalizationService` maps the persisted language preference to that manager and
formats view-model messages with the selected culture. User song titles, playlist
names, and provider error details remain the data supplied by their sources.

## Platform behavior

| Area | Windows | macOS | Linux |
| --- | --- | --- | --- |
| Window | Native decorations | Native traffic-light controls | Window-manager decorations |
| Folder import | Native storage picker | Native storage picker | Avalonia storage picker / available desktop integration |
| Import shortcut | Ctrl+O | Command+O | Ctrl+O |
| Settings and storage | Roaming/local AppData | Application Support / Caches | XDG config/data/cache |
| Media controls | Windows SMTC plugin | Now Playing plugin and AppKit helper | Regular desktop playback |

The UI uses platform font fallback and scales with Avalonia's DPI handling.
It does not assume drive letters, Windows shell APIs, or a fixed desktop theme.
Platform media plugins are selected by the build OS and checked again by their
manifests at runtime. The macOS native helper requires Xcode Command Line Tools.
Publish on the destination OS to include its native helper and platform plugin:

```sh
dotnet publish src/LemonNeo.Desktop --configuration Release --output artifacts/desktop
```

This produces a framework-dependent desktop directory requiring the .NET 10 and
ASP.NET Core 10 shared runtimes (both are included with the .NET SDK).
Installer packaging, macOS application-bundle signing, and notarization are
separate release work. Linux needs a graphical session and Avalonia's usual
font/X11 or Wayland dependencies for native operation.

## Music UI and Console command coverage

The sidebar contains the library, search, online services, tools, settings, and
persisted local playlists. Now playing opens from the playback bar's song information,
and the queue opens from its right-side queue button. Navigation remains visible at the
minimum window size, with the playlist list scrolling independently. Component
previews are available from Tools. Double-click or press Enter on a selected track to play. The bottom
bar in the right column offers play/pause, previous/next, mode switching and seeking. Playback is restored on
startup and starts only after an explicit play action. Selecting an existing queue
row preserves queue item identities and the chosen source.

Desktop calls the same application services and provider contracts as Console:

| Console command | Desktop entry |
| --- | --- |
| `help` | Tools → Help, with native GUI actions and platform shortcuts |
| `version`, `plugins`, `providers`, `players` | Tools → Plugins: version, complete manifests, capabilities, namespaces, accepted IDs, engines, priorities and load failures |
| `library`, `paths` | Tools → Plugins: playlist/unique-track/item statistics and all shared paths |
| `startup [show\|set\|reset]` | Tools → Console startup script: edit/save, import a file, reload, reset |
| `auth`, `login`, `logout` | Online services → Accounts: authentication state, cookie login, logout |
| `search [--source]` | Top toolbar search → search results: keyword across all providers; source namespaces are selected automatically from loaded playback-source capabilities |
| `add <playlist> <provider> <id>` | Search → selected result → destination playlist → Add to playlist; direct ID lookup remains a Console action |
| `remote-playlists`, `import` | Online services → Remote playlists: browse named collections and import a selected playlist or an ID |
| `import-folder` | Sidebar + → New playlist → Import from folder: native picker; edit name/description and confirm creation |
| `playlists`, `playlist`, `show-playlist` | Sidebar playlist list and selected playlist's ordered track list |
| `create-playlist`, `update-playlist`, `delete-playlist` | Sidebar + button and Library → Playlist actions; name/description editor and deletion confirmation |
| `show-track`, `create-track`, `update-track` | Library → Track details / Track actions → New track: all snapshot fields, item identity and external IDs |
| `external-ids [add\|update\|remove]` | Track details: add, edit and remove namespace/kind/value rows, then save |
| `remove`, `move-track` | Library → Track actions: remove, move up/down or enter a destination position in the full playlist order, including when filtered |
| `metadata [namespace ...]`, `enrich` | Track details → Metadata: namespace targets, matches/errors, enrichment preview and explicit Apply |
| `match-playlist [namespace ...]` | Selected playlist → Match playlist: namespace targets, batch enrichment, saved results and provider errors |
| `lyrics` | Track details → Lyrics: complete lyrics, source, synchronization flags, translation, romanization and provider errors |
| `sources` | Track details → Playable sources: candidates, identities, confidence, resolution method, location and provider errors |
| `play [--source]`, `play-playlist [start] [--source]` | Track double-click / Enter, Play / Play all; Search uses automatic source discovery and Library offers a playback-source selector |
| `play-current`, `pause`, `resume`, `previous`, `next` | Playback bar and queue row selection; `stop` remains available in Console |
| `mode`, `now-playing` | Playback mode icon and clickable song information → Now playing: sequential/repeat/shuffle choices, identity, queue position, engine, source and timeline |
| `queue [clear]` | Playback bar queue button → queue card and Clear queue |
| `sync status\|pull\|push` | Tools → Synchronization: status/revisions, pull/merge, push and conflict reports |
| `save`, `exit` | Tools → Save and native window close; edits also save immediately |

Provider selectors list only services implementing the necessary capability.
Search has no provider, source, or native-ID controls. It queries all search
providers against namespaces advertised by the loaded playback-source providers,
using their accepted ID schemes and native provider identities. Providers with
source filtering are queried only for supported namespaces; other providers are
queried once and their results filtered by these capabilities. Search playback
discovers sources automatically, independently of Library's manual source choice.
Actual source availability is checked when playing. Console's explicit search
filters and native-ID lookup remain available.
Cookie input is masked and cleared after every sign-in attempt; authentication
persistence belongs to the provider's existing credential vault. Remote playlist
collection headings are retained, including empty collections. An unsuccessful
remote import does not create a local playlist.

Track edits address playlist **item UUIDs**, so duplicated tracks remain
independent. Filtering retains original item numbers for move operations. The
editor validates required fields, finite nonnegative durations, absolute artwork
URIs and manually introduced duplicate external-ID schemes without rejecting
existing alternatives from metadata providers. It stays open on validation or
save failure, and a failed new-item save can be retried without adding a duplicate.
Metadata enrichment is previewed first; applying it changes the editor and saving
persists that item. Batch matching enriches all occurrences of matching tracks
with up to three requests in flight and surfaces provider errors.

Playlist track counts and total duration appear beneath its title and optional
description. Empty descriptions take no space. The library overview omits its
subtitle and shows statistics below the title. Search and queue have their own
headers and statistics, and the library overview hides track/playlist action menus.
Track rows give the title and album proportional columns, with a shared
content-sized duration column that keeps rows aligned as the window changes size.
Track lists have no statistics, shortcut-hint, or persistent status footer.
Long-running operations expose a compact progress indicator and Cancel in the
page header, with the operation status available as a tooltip. Search and metadata work
do not prevent pausing playback or seeking. Closing cancels outstanding work,
waits for image/lyric/playback tasks, saves state and then shuts down plugins.

Console and Desktop share `LyricsTimeline` for line/word selection, romanization
and the translation's own timeline. Avalonia text runs highlight reached words
and wrap long lines using the platform font fallback. Pausing retains highlights;
stopping clears active lyric rows. Plain lyrics and optional auxiliary text are
also displayed. The full lyrics lookup remains available in Track details.

The playback bar and now-playing view share the current track's artwork. The
desktop loads absolute `file:` and HTTP/HTTPS artwork URIs asynchronously, with
a 15-second timeout and a 16 MiB limit, and decodes images to a 512-pixel width.
Changing the artwork URI cancels the previous request and discards late results;
timeline ticks and play/pause changes reuse the displayed bitmap. Missing or
unreadable covers use the shared theme-aware placeholder. Local paths are read
via `Uri.LocalPath` on all platforms. The local music plugin currently imports
filename metadata only; embedded audio artwork extraction is not implemented.

The startup editor configures the shared **Console** script; Desktop opens its
GUI directly and does not execute Console scripts. Console command-line editing,
history and terminal rendering are represented by GUI controls and native text
editing. Synchronization uses the same `mock-git` demonstration provider currently
registered by Console; a real remote Git backend is not implemented in this
repository. Online authentication/import/source availability remains subject to
each loaded plugin and its upstream service.

## Verification

```sh
dotnet test tests/LemonNeo.Desktop.Tests --configuration Release
```

The headless xUnit suite loads the real XAML templates and tests theme/culture
changes in existing views, configuration persistence and failures, contrast,
local plugin import, playback command wiring, duplicate queue selection, local
and network artwork loading, cancellation, restored covers in both views, and
rendering of every page at minimum and default sizes in both palettes. Additional
integration checks cover capability filtering, automatic search scopes, source ID
kinds and native identities, automatic search playback, masked
account input clearing, grouped remote imports, playlist CRUD, duplicate item
editing/reordering, external-ID validation, failed-save retries, metadata preview
and batch matching, word/translation timelines, selected sources, cancellation
with responsive playback, synchronization, and shared startup-file persistence.
Navigation checks cover lazy loading, page-owned headers, preserved back/forward
views, independent playlist/queue state, settings and library operations during
pending searches, directional transform/opacity animation, cancellation cleanup,
queued navigation and reduced motion.
Layout checks verify the full-height sidebar, right-column playback, compact
toolbar, centered playback controls and icons, and the boundary timeline. Playback
interaction checks cover click/drag/keyboard seeking, mode icons, clickable song
information and the loading overlay. Global search checks cover Enter, the search
button, lazy loading and navigation during pending requests.
Queue card checks cover lazy loading, independent filters, duplicate item
identities and source preservation, current row highlighting, right-edge placement
after resizing, live theme/language changes, outside dismissal, Escape, clear,
slide animation and cancellation cleanup.
All new tabs and action flyouts are rendered in English/Chinese and both palettes.
Tests use deterministic providers and an in-memory player to avoid network-account
and audio-device requirements.

Set `LEMONNEO_PREVIEW_DIRECTORY` to export rendered PNGs during the tests. The
Desktop GitHub Actions workflow builds, tests, and publishes on all three OSes.
Headless tests do not substitute for testing native pickers, window management,
audio devices, and media keys on each destination platform.
