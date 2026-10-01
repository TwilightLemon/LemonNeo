# Lemon Neo phase-one prototype

This repository contains the console and class-library prototype described in
[`prototype.md`](prototype.md). It targets .NET 10.

Build and start the interactive host:

```powershell
git submodule update --init --recursive
dotnet build LemonNeo.slnx
dotnet run --project src/LemonNeo.Console
```

The build deploys the default plugins beside the console under `plugins/`:

- `LemonNeo.Plugin.LocalMusic` scans folders and resolves local files.
- `LemonNeo.Plugin.Bass` decodes and plays local files and direct HTTP audio
  streams through BASS/BASS.NET.
- `LemonNeo.Plugin.GdStudioNetease` resolves Netease song IDs from any metadata
  provider to direct audio URLs through GDStudio.
- `LemonNeo.Plugin.Lyricify` matches metadata across supported online services and
  retrieves synchronized lyrics through Lyricify Lyrics Helper.
- `LemonNeo.Plugin.MockMusic` supplies deterministic prototype data.
- `LemonNeo.Plugin.Netease` authenticates with a Netease Music browser cookie and
  imports the user's online playlists.

The console discovers all six dynamically and has no runtime assembly reference
to the music/player plugin implementations.

Run the automated phase-one walkthrough with an isolated library directory:

```powershell
dotnet run --project src/LemonNeo.Console -- --demo --library .demo-library
```

The default library uses the platform's per-user application-data directory. Run
`paths` to inspect every resolved location. Use `--plugins <path>` or
`--library <path>` to override either runtime location.

## Application data and settings

`LemonNeo.Configuration` owns paths and persistent JSON stores shared by the host
and plugins. Defaults follow each operating system's conventions:

- Windows: roaming AppData for settings; local AppData for library data and cache.
- macOS: `~/Library/Application Support/LemonNeo` for settings and data, and
  `~/Library/Caches/LemonNeo/Cache` for cache.
- Linux: `XDG_CONFIG_HOME`, `XDG_DATA_HOME`, and `XDG_CACHE_HOME`, with their
  standard home-directory fallbacks.

The roots can be overridden with `LEMONNEO_CONFIG_HOME`, `LEMONNEO_DATA_HOME`,
`LEMONNEO_CACHE_HOME`, and `LEMONNEO_LIBRARY_HOME`. Application settings are in
`appsettings.json`; each plugin gets an isolated `Plugins/<plugin-id>/settings.json`
store and `Cache/Plugins/<plugin-id>` cache directory. Cache contents are
disposable and must not contain durable state.

Plugin credentials are kept separately under `Credentials/<plugin-id>` and are
encrypted with an application-scoped ASP.NET Core Data Protection key ring. On
Unix-like systems the credential/key directories and credential files are also
restricted to the current user. This prevents accidental plaintext disclosure;
it is not an operating-system keychain and cannot protect against another process
already running with the same user account.

## Netease user library

Sign in to `music.163.com` in a browser, copy the site's complete Cookie request
header, then run `login netease`. Omitting the cookie argument opens a prompt so
the value is not included in shell history:

```text
login netease
auth netease
remote-playlists netease
import netease <playlist-id>
playlists
play-playlist "<playlist-name>" --source gdstudio-netease
logout netease
```

Login validates the cookie against the Netease account endpoint before storing
it. The imported tracks retain Netease `song-id` identifiers, allowing the
GDStudio provider to resolve playback even though this user-library provider does
not supply audio URLs itself.

Import a folder as a Lemon Neo playlist, then inspect and play its tracks:

```text
import-folder "D:\Musics\Instrumental" "Instrumental"
playlists
show-playlist <playlist-id>
play local <track-id>
pause
resume
stop
```

Load an entire library playlist into the playback queue and control navigation
and playback order:

```text
play-playlist "Instrumental" 1
previous
next
mode sequential
mode repeat
mode shuffle
now-playing
queue
queue clear
```

The playback queue, selected item, required source, playback mode, and shuffle
cursor are stored in `playback.json` beside the library. After restarting, use
`play-current` to play the restored selection. Audio is not resumed automatically.

`PlaybackSessionService` is the application-level playback authority. It exposes
the current track, player state, timeline progress, state/timeline events, and a
natural-completion event. Because BASS does not provide a completion callback to
the application contract, this service polls its read-only timeline and advances
the queue when the channel ends. The same event surface is intended for a future
SMTC integration plugin.

Match an imported track to one or more online catalogues, persist reliable
metadata, retrieve lyrics, and inspect all currently playable sources:

```text
metadata local <track-id>
metadata local <track-id> netease qqmusic lrclib
enrich local <track-id>
lyrics local <track-id>
sources local <track-id>
```

The metadata provider currently searches QQ Music, Netease Music, Kugou Music,
Apple Music, Spotify, Musixmatch, Soda Music, and LRCLIB. Individual services can
be unavailable or require their own credentials; failures are isolated and shown
per source.

The Lyricify plugin also participates in the normal `search <keyword>` command.
Catalog search is currently limited to QQ Music, Netease Music, and Kugou Music;
each returned track retains its source-specific external IDs for lyrics and later
playback-source discovery.

Optional service credentials can be supplied without storing them in the library
or plugin configuration files:

```powershell
$env:LEMONNEO_SPOTIFY_SP_DC = '<Spotify sp_dc cookie value>'
$env:LEMONNEO_APPLE_MUSIC_USER_TOKEN = '<Apple Music media user token>'
$env:LEMONNEO_MUSIXMATCH_USER_TOKEN = '<Musixmatch user token>'
```

External track identifiers are qualified by `(namespace, authority, kind)`. A
playback provider may consume an identifier only when it explicitly declares that
exact scheme. If schemes differ, Lemon Neo searches the target provider and uses
the target provider's native track ID instead. This prevents, for example, a QQ
Music numeric song ID from being mistaken for a song MID or for an ID issued by a
different plugin.

Run `players` to inspect the active player implementations. BASS has priority for
local files and direct HTTP audio streams; the console player remains the fallback
for non-audio prototype sources.

GDStudio playback is resolved across plugins: Lyricify searches Netease and emits
an authority-qualified `song-id`, the GDStudio provider accepts Netease `song-id`
values issued by any metadata provider, and BASS plays the returned direct HTTP
audio stream. The API request and audio stream both carry the recommended browser
user agent.

Use `--source` to require a specific playback-source provider. A required source
does not fall back to another provider if resolution fails:

```text
play lyricify netease:song-id:29572502 --source gdstudio-netease
```

The mock provider includes a metadata-only playlist of popular The Weeknd songs.
The following commands demonstrate importing unavailable tracks, explicitly
observing the failed mock source, batch-matching Netease IDs, and then playing the
same library track through GDStudio:

```text
import mock weeknd
play mock track-001 --source mock
match-playlist "The Weeknd Essentials" netease
play mock track-001 --source gdstudio-netease
```

The BASS plugin carries the BASS.NET 2.4.14.1 wrapper, BASS 2.4.16, and the FLAC
2.4.5 decoder copied from the referenced Lemon-App resources. Applications that
redistribute these binaries must comply with the applicable BASS/BASS.NET license.

The Lyricify plugin uses the Apache-2.0-licensed
[Lyricify Lyrics Helper](https://github.com/WXRIW/Lyricify-Lyrics-Helper). Its
upstream source is retained under `external/Lyricify-Lyrics-Helper` as a git
submodule; the runtime plugin references the published `Lyricify.Lyrics.Helper`
NuGet package. See `plugins/LemonNeo.Plugin.Lyricify/THIRD-PARTY.md`.
