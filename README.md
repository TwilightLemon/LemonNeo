# Lemon Neo phase-one prototype

This repository contains the console and class-library prototype described in
[`prototype.md`](prototype.md). It targets .NET 10.

Build and start the interactive host:

```powershell
dotnet build LemonNeo.slnx
dotnet run --project src/LemonNeo.Console
```

The build deploys the default plugins beside the console under `plugins/`:

- `LemonNeo.Plugin.LocalMusic` scans folders and resolves local files.
- `LemonNeo.Plugin.Bass` decodes and plays local files through BASS/BASS.NET.
- `LemonNeo.Plugin.MockMusic` supplies deterministic prototype data.

The console discovers all three dynamically and has no runtime assembly reference
to the music/player plugin implementations.

Run the automated phase-one walkthrough with an isolated library directory:

```powershell
dotnet run --project src/LemonNeo.Console -- --demo --library .demo-library
```

The default library is `LemonLibrary/` under the current working directory.
Use `--plugins <path>` or `--library <path>` to override either location.

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

Run `players` to inspect the active player implementations. BASS has priority for
local `FilePlaybackSource` values; the console player remains the fallback for
mock HTTP sources.

The BASS plugin carries the BASS.NET 2.4.14.1 wrapper, BASS 2.4.16, and the FLAC
2.4.5 decoder copied from the referenced Lemon-App resources. Applications that
redistribute these binaries must comply with the applicable BASS/BASS.NET license.
