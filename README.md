<div align=center>
<img src="https://github.com/TwilightLemon/LemonNeo/blob/main/src/LemonNeo.Desktop/Resources/Icons/App.ico?raw=true" width="128" height="128"/>

# Lemon Neo (Preview)
A cross-platform music player bringing your favorite music together.  
✨ *Keep Simple and Delicate* ✨

[<img src="https://img.shields.io/badge/license-GPL%203.0-yellow"/>](LICENSE)
![C#](https://img.shields.io/badge/lang-C%23-orange)
![Avalonia UI](https://img.shields.io/badge/UI-Avalonia%20UI-b33bb3)
![GitHub Repo stars](https://img.shields.io/github/stars/TwilightLemon/LemonNeo)  


</div>

![overview](https://github.com/TwilightLemon/Data/blob/master/lemonneo/overview.webp?raw=true)
![playback](https://github.com/TwilightLemon/Data/blob/master/lemonneo/playback.webp?raw=true)
![lightmode](https://github.com/TwilightLemon/Data/blob/master/lemonneo/lightmode.webp?raw=true)
---

## Introduction

Lemon Neo gathers your music into one library. Bring local files, sign in to the online services you already use, and keep playlists, queue, lyrics, and artwork together on your own machine — nothing is locked to a single provider.

Everything beyond the library itself comes from plugins: sources, decoders, lyrics and metadata, account sign-in, and operating-system integration. Add or remove any of them by dropping a folder into `plugins/`, so the app stays small and no music service is built into it.

A desktop app and a terminal client share the same core, library, and settings. Lemon Neo is a free software under the GPL-3.0 and currently in preview — expect rough edges and changing behavior.

## Requirements

- [.NET SDK 10.0](https://dotnet.microsoft.com/download) or later. 
- Windows 10 or later, macOS 13 or later.

P.S. Linux is not currently supported due to the lack of audio output and window behavior support in Avalonia UI on that platform. It may be possible to run the console client on Linux, but it is untested.

## Build and run

```sh
git clone --recursive https://github.com/TwilightLemon/LemonNeo.git
cd LemonNeo

dotnet build LemonNeo.slnx

# Desktop app
dotnet run --project src/LemonNeo.Desktop

# Terminal client
dotnet run --project src/LemonNeo.Console
```

Plugins are deployed into the host's `plugins/` directory automatically; pass `-p:SkipHostPlugins=true` to build a host without them. The console host accepts `--plugins <dir>`, `--library <dir>`, and `--startup <file>`.

## Features

### Cross-platform

The same app, library, and features on Windows and macOS:

- **Native where it matters** — window chrome, shortcuts, and sidebar style follow each platform; closing the window keeps the music playing from the tray icon (Windows) or the Dock menu (macOS).
- **System media integration** — hardware media keys, system media controls, and the OS "now playing" panel on both platforms (SMTC on Windows, Now Playing on macOS).
- **Desktop lyrics that behave natively** — the floating lyrics window never steals focus on Windows and joins every Space, including fullscreen apps, on macOS.
- **Identical everywhere else** — themes, artwork-derived colors, the animated mesh background, the audio visualizer, and the fullscreen lyrics page work the same on both.

### Pluggable services

Everything beyond your library is a plugin: sources, decoders, lyrics, metadata, accounts, and system integration. Run it entirely local with a folder of files and a decoder, or stack online services on top — and uninstall any of it by removing a folder. A plugin that is incompatible or fails to start is reported with the reason and skipped, so the rest of the app keeps working.

→ Capability reference and authoring guide: [docs/plugins.md](docs/plugins.md)

### Multi-source playback

A track is not tied to one service: it carries its own identity plus the external IDs it has been matched to (ISRC, or per-service IDs such as `netease`, `qqmusic`, or `spotify`). When you press play:

1. A **local file** wins, if one exists.
2. Otherwise a **cached copy** is used.
3. Otherwise every plugin that accepts the track's identity is asked, from the **highest quality down**.
4. If nothing resolves, the track is **expanded through metadata matching** and tried again — so a track you only have from one service can still play through another.

The playback details panel shows the source, decoder, quality, and cache state actually in use.

### Cross-namespace metadata matching

Metadata is not tied to whichever service a track came from:

- Matches are scored with a **confidence value and match level**, filtered by a **minimum confidence**, and aimed at the **source namespaces** you choose.
- Accepting a match **adds external IDs** instead of overwriting your metadata, so a track gains identities — and playback options — over time.
- Whole playlists can be matched at once, and the track editor lets you hand-edit any field, including external IDs.

### Highly customizable

- **Appearance** — light, dark, or system; accent palettes or colors derived from the track's artwork; the mesh background and visualizer tuned with presets or turned off; a reduced-motion option.
- **Lyrics** — font size, bold, alignment, translation and romanization toggles, one or two lines, and a floating desktop lyrics window you can place anywhere.
- **Playback** — preferred audio quality and decoder, playback modes, and source choice when several plugins can play the same track.
- **Library** — drag-and-drop playlists, folder import that picks up embedded covers and tags, remote playlist import and export, and moving your library or cache anywhere without restarting.

---

## Built-in plugins

| Plugin | Role | Platforms |
| --- | --- | --- |
| `LemonNeo.Plugin.Bass` | Playback engine on BASS + BASSFLAC | all |
| `LemonNeo.Plugin.WindowsSmtc` | System Media Transport Controls | Windows |
| `LemonNeo.Plugin.MacNowPlaying` | macOS Now Playing | macOS |
| `LemonNeo.Plugin.Netease` | Netease Cloud Music user library | all |
| `LemonNeo.Plugin.QqMusic` | QQ Music user library | all |
| `LemonNeo.Plugin.GdStudioNetease` | Alternative Netease playback source | all |
| `LemonNeo.Plugin.Lyricify` | Lyrics and metadata matching | all |
| `LemonNeo.Plugin.MockMusic` | Reference implementation for plugin authors | all |


## Configuration and storage

Lemon Neo follows each platform's conventions; the desktop app's defaults are below, and **Settings → Storage** shows the paths currently in use. Moving the library or the cache takes effect immediately, without restarting.

| Content | Windows | macOS | Linux |
| --- | --- | --- | --- |
| Configuration | `%APPDATA%\LemonNeo` | `~/Library/Application Support/LemonNeo` | `$XDG_CONFIG_HOME/LemonNeo` (`~/.config/LemonNeo`) |
| Library | `%LOCALAPPDATA%\LemonNeo\Library` | `~/Library/Application Support/LemonNeo/Library` | `$XDG_DATA_HOME/LemonNeo/Library` (`~/.local/share/LemonNeo/Library`) |
| Cache | `%LOCALAPPDATA%\LemonNeo\Cache` | `~/Library/Caches/LemonNeo/Cache` | `$XDG_CACHE_HOME/LemonNeo/Cache` (`~/.cache/LemonNeo/Cache`) |
| Data and credentials | `%LOCALAPPDATA%\LemonNeo` | `~/Library/Application Support/LemonNeo` | `$XDG_DATA_HOME/LemonNeo` (`~/.local/share/LemonNeo`) |
| Plugins | `<app>\plugins` | `<app>/plugins` | `<app>/plugins` |

Environment variables override the resolved directories; each one is used as-is, not appended to:

| Variable | Overrides |
| --- | --- |
| `LEMONNEO_CONFIG_HOME` | Configuration directory |
| `LEMONNEO_LIBRARY_HOME` | Library directory |
| `LEMONNEO_CACHE_HOME` | Cache directory |
| `LEMONNEO_DATA_HOME` | Data directory (credentials and data-protection keys) |

What lives where:

- **Library** — `library.json` (the playlist index) and `playlists/<id>.json`. If a configured library cannot be opened, the app reports it and falls back to the default.
- **Configuration** — one JSON file per settings entity, grouped by package: the desktop app's preferences (including storage overrides) in `LemonNeo.Desktop/` — `AppearancePreferences.json`, `LyricsPreferences.json`, and so on — plus one folder per plugin.
- **Cache** — `Artwork/`, `Images/`, `Lyrics/`, `Music/` (cached audio), and per-plugin private folders.
- **Data** — saved sign-in credentials per plugin, and the keys used to encrypt them.

The library and cache must be writable and separate from each other, from configuration, plugins, and credentials; a folder holding unrelated files is rejected rather than used.

## Documentation

- [Plugins](docs/plugins.md) — capability reference, manifest fields, validation rules, and an authoring guide
- [Package naming](docs/package-naming.md) — plugin IDs and configuration entity naming
- [Desktop localization](docs/localization.md) — conventions for adding UI languages
- [BASS format support](docs/bass-format-support-todo.md) — decoder coverage notes

## License

Licensed under the [GNU General Public License v3.0](LICENSE).

Bundled and third-party components keep their own licenses. Each plugin declares its dependencies in `plugin.json` and ships the required notices alongside its package.
