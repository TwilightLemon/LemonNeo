# Plugins

Lemon Neo's host owns the library, playback session, caches, and user interface. Everything that
touches the outside world — music services, decoders, lyrics, metadata, accounts, operating-system
media integration — is provided by plugins.

A plugin is a directory containing a `plugin.json` manifest and an entry assembly. It is loaded into
its own `AssemblyLoadContext`, so it can ship whatever it depends on without conflicting with the
host or with other plugins, and it can be shut down and unloaded independently.

## Capabilities

A plugin registers any combination of the following services on the host. A single plugin often
provides several: a music service plugin typically implements search, lookup, playlists, and
authentication together.

| Capability | Contract | What it adds to the app |
| --- | --- | --- |
| Music provider | `IMusicProvider` | Identity for a source of tracks |
| Search | `ISearchProvider` | Keyword search, with paging and limits |
| Search namespaces | `ISearchNamespaceProvider` | Restricts a search to one of the plugin's sources |
| Track lookup | `ITrackLookupProvider` | Exact retrieval by native ID, without approximating through search |
| Playlist source | `IPlaylistProvider` | Remote playlists and their tracks |
| Playlist collections | `IPlaylistCollectionProvider` | Named groups of user playlists (owned, followed, purchased, …) |
| Playlist export | `IPlaylistExportProvider` | Pushes a local playlist back to the service |
| Playback source | `IPlaybackProvider` | Resolves a track to a playable stream at a requested quality |
| Lyrics | `ILyricsProvider` | Timed lyrics, translations, romanization |
| Metadata | `IMetadataProvider` | Cross-source matching with confidence scores |
| Authentication | `IAuthenticationProvider` | Cookie, browser, QR code, device code, or password sign-in |
| Decoder | `IPlayerProvider` | Playback engine and the formats it understands |
| System integration | `IInteractivePlugin` | Publishes playback to the OS and routes its controls back |

Each capability is discovered through its interface, so the host knows at runtime which plugins can
search, which can resolve playback, which can supply lyrics, and which can play a given file. The
`Capabilities` section of each plugin's detail page lists exactly what it registered.

### How capabilities are combined at runtime

- **Playback resolution** (`PlaybackSourceDiscoveryService`). A track carries a native ID plus any
  number of external IDs (ISRC, and per-service namespaces such as `netease`, `qqmusic`, `spotify`).
  Playback tries the track's own identities first, then — if enabled — expands the track through
  metadata matching and retries with the matched identities, at each quality in descending order.
  An existing cache entry short-circuits the search, and an explicit local file always wins.
  Resolution is retried independently per provider, and failures are reported per provider rather
  than aborting the whole attempt.
- **Metadata matching** (`MetadataService`). A lookup names the target namespaces, then queries each
  metadata provider and returns candidate matches with a confidence score and match level. The host
  applies a minimum confidence and reports per-source errors separately, so one failing source does
  not hide the others.
- **Quality classification** (`AudioQualities`). Measured stream properties take precedence over a
  provider's declared quality; bitrate alone never establishes lossless or HiRes.
- **Interaction** (`IInteractivePlugin`). The plugin receives a platform-independent snapshot of the
  host's playback session and sends commands back through the same channel, so the host remains the
  single authority over playback.

## Manifest

```json
{
  "id": "LemonNeo.Plugin.MySource",
  "name": "My Music Source",
  "version": "1.0.0",
  "entryAssembly": "LemonNeo.Plugin.MySource.dll",
  "minimumHostVersion": "0.1.0",
  "platforms": ["windows", "macos", "linux"],
  "permissions": ["network"],
  "dependencies": [
    {
      "name": "Example.Client",
      "version": "2.0.0",
      "license": "MIT",
      "website": "https://example.com/example-client"
    }
  ]
}
```

| Field | Required | Notes |
| --- | --- | --- |
| `id` | yes | Must be a PascalCase package name (see [package naming](package-naming.md)) and must equal `PluginDescriptor.Id` |
| `name` | yes | Display name shown in the app |
| `version` | yes | Must parse as a version |
| `entryAssembly` | yes | Must resolve to a file inside the plugin's own directory |
| `minimumHostVersion` | yes | Plugins requiring a newer host are refused with a clear error |
| `platforms` | no | Any of `windows`, `macos`, `linux`; empty means all |
| `permissions` | no | Declared capabilities, shown to the user (for example `network`, `file-system`, `audio-device`, `native-code`, `playback.read`, `playback.control`, `system.media`) |
| `dependencies` | no | Third-party packages, each with name, version, and license; `website` optional |

Every dependency must declare a license because the About page displays these declarations to users.
Ship the notices those licenses require alongside the plugin, and keep versions current when
releasing.

## Validation and failure handling

A manifest is rejected before any code runs when:

- `id`, `entryAssembly`, or `version` is missing, or the version does not parse.
- The ID is not a valid PascalCase package name.
- `platforms` contains anything other than `windows`, `macos`, or `linux`.
- `minimumHostVersion` is missing or higher than the host's version.
- A dependency is missing its name, version, or license, or has a non-HTTP(S) website.

A plugin is rejected while loading when:

- The package ID is already loaded (case-insensitive comparison).
- The entry assembly is missing or resolves outside the plugin directory.
- The assembly does not export exactly one concrete `ILemonPlugin`.
- The instance cannot be created (a public parameterless constructor is required).
- `PluginDescriptor.Id` does not match the manifest `id`.

Plugins whose `platforms` do not include the current operating system are skipped quietly rather
than reported as failures.

If a plugin throws during initialization, the providers and decoders it already registered are
removed, it is asked to shut down, and its configuration is released — no partial state is left in
the host. Failures never stop other plugins from loading; they are collected into a load report
(`IPluginDiagnostics.LastReport`) and surfaced in the app.

## Writing a plugin

Reference `LemonNeo.Core` and `LemonNeo.Abstractions` only — never a host project. The host provides
a logger factory, an HTTP client factory, per-package settings, a credential vault, and a private
cache directory through `IPluginContext`.

```csharp
using LemonNeo.Abstractions.Plugins;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Music;

public sealed class MyPlugin : ILemonPlugin
{
    public PluginDescriptor Descriptor { get; } =
        new("LemonNeo.Plugin.MySource", "My Music Source", "1.0.0");

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.RegisterProvider(new MyProvider(context.HttpClientFactory));
        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class MyProvider(IHttpClientFactory httpClientFactory)
    : IMusicProvider, ISearchProvider
{
    public string Id => "mysource";
    public ProviderDescriptor Descriptor { get; } = new("My Source", "Example music provider");

    public async Task<SearchResult> SearchAsync(
        SearchRequest request, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(nameof(MyProvider));
        var tracks = await QueryMyServiceAsync(client, request.Keyword, cancellationToken);
        return new SearchResult(Id, tracks);
    }

    // Maps the service's response into LemonNeo.Core.Music.Track values.
    private static Task<IReadOnlyList<Track>> QueryMyServiceAsync(
        HttpClient client, string keyword, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
```

Notes for plugin authors:

- Register playback sources with `RegisterPlayer` / `RegisterProvider`; implement
  `IAuthenticationProvider` alongside them if the service needs sign-in.
- Read `IPluginContext.CacheDirectory` at the start of each operation instead of caching the path —
  the user can change the cache location while the plugin is running.
- Settings entities are stored one JSON file per type, named after the type
  (`AccountSettings.json`), with camelCase properties.
- Decoders implement `IPlayer.PrepareAsync` to open a request-owned `IPreparedPlayback`
  without changing the active stream or starting audio. Slow I/O must run outside the active
  player's lock. `Activate(volume)` must be brief and perform no network I/O. Disposing a
  preparation must release only its own stream, including after activation. Decoder plugins
  built against the previous player contract must be rebuilt and implement preparation.
- Interactive plugins subscribe to `MetadataChanged`, `StateChanged`, `TimelineChanged`,
  `QueueChanged`, and `VolumeChanged` as needed. A selected track is published in `Loading`
  before source discovery; `Playing` is published only after activation succeeds.
  `ExecuteAsync` submits media commands without waiting for source discovery.
  `InteractivePluginBase.UpdateAsync` receives `PlaybackUpdateKind` flags so a coalesced
  timeline update does not discard a pending metadata or state change.
- Plugins are expected to shut down cleanly: unsubscribe from `IPlaybackInteraction` events,
  cancel outstanding work, and release resources. Subscribers must return promptly since events can
  arrive on background threads.
- `LemonNeo.Plugin.MockMusic` is a small reference implementation to start from.

## Built-in plugins

| Plugin | Role | Platforms |
| --- | --- | --- |
| `LemonNeo.Plugin.Bass` | Playback engine on BASS + BASSFLAC, with native libraries for Windows, Linux, and macOS | all |
| `LemonNeo.Plugin.WindowsSmtc` | System Media Transport Controls integration | Windows |
| `LemonNeo.Plugin.MacNowPlaying` | macOS Now Playing integration | macOS |
| `LemonNeo.Plugin.Netease` | Netease Cloud Music user library | all |
| `LemonNeo.Plugin.QqMusic` | QQ Music user library | all |
| `LemonNeo.Plugin.GdStudioNetease` | Alternative Netease playback source | all |
| `LemonNeo.Plugin.Lyricify` | Lyrics and metadata matching | all |
| `LemonNeo.Plugin.MockMusic` | Reference implementation for plugin authors | all |

The first-party plugins are built and deployed into the host's `plugins/` directory automatically by
`build/LemonNeo.Plugins.props` and `build/LemonNeo.Plugins.targets`. Pass
`-p:SkipHostPlugins=true` to build a host without them.
