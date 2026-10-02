using System.Text;
using LemonNeo.Application;
using LemonNeo.Configuration;
using LemonNeo.Core;
using LemonNeo.Playback;
using LemonNeo.Plugin.Abstractions;
using LemonNeo.Plugin.Runtime;

namespace LemonNeo.ConsoleApp;

internal sealed partial class CommandProcessor(
    PluginLoader plugins,
    IProviderRegistry providers,
    IPlaybackSourceProviderRegistry playbackSources,
    IPlayerRegistry playersRegistry,
    IAppPathProvider appPaths,
    ConsoleOptions options,
    LibraryService library,
    SearchService search,
    TrackLookupService trackLookup,
    MetadataService metadata,
    LyricsService lyrics,
    PlaylistService playlists,
    ImportService importer,
    FolderImportService folderImporter,
    PlaybackSessionService playback,
    PlaybackSourceDiscoveryService sourceDiscovery,
    SyncService sync,
    StartupCommands startup,
    CommandCompletionService completions,
    CommandHistory history)
{
    private readonly TextWriter _output = global::System.Console.Out;

    public async Task<bool> RunStartupAsync(CancellationToken cancellationToken)
    {
        var lines = await startup.LoadAsync(cancellationToken);
        for (var index = 0; index < lines.Length; index++)
        {
            try
            {
                if (!await ExecuteAsync(lines[index], cancellationToken))
                {
                    return false;
                }
            }
            catch (Exception exception) when (exception is LemonNeoException or ArgumentException or
                                             InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                _output.WriteLine($"Startup error ({startup.FilePath}:{index + 1}): {exception.Message}");
            }
        }

        return true;
    }

    public async Task RunInteractiveAsync(CancellationToken cancellationToken)
    {
        await history.LoadAsync(cancellationToken);
        _output.WriteLine("Type a command below. ↑↓ browses history when empty and selects suggestions while typing.");
        var editor = InteractiveLineEditor.IsSupported
            ? new InteractiveLineEditor(completions.Complete, history.Entries)
            : null;
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            if (editor is null)
            {
                _output.Write("lemon > ");
                line = await global::System.Console.In.ReadLineAsync(cancellationToken);
            }
            else
            {
                line = editor.ReadLine(cancellationToken);
            }

            if (line is null)
            {
                await library.SaveAsync(cancellationToken);
                await playback.SaveAsync(cancellationToken);
                return;
            }

            try
            {
                await history.RecordAsync(line, cancellationToken);
                if (!await ExecuteAsync(line, cancellationToken))
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is LemonNeoException or ArgumentException or
                                             InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                _output.WriteLine($"Error: {exception.Message}");
            }
        }
    }

    public async Task<bool> ExecuteAsync(string line, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
        {
            return true;
        }

        var arguments = Tokenize(line);
        if (arguments.Count == 0)
        {
            return true;
        }

        var command = arguments[0].ToLowerInvariant();
        switch (command)
        {
            case "help":
                PrintHelp();
                break;
            case "plugins":
                PrintPlugins();
                break;
            case "version":
                var version = typeof(Program).Assembly.GetName().Version;
                _output.WriteLine($"Lemon Neo {version?.ToString(3) ?? "unknown"}");
                break;
            case "library":
                PrintLibrary();
                break;
            case "startup":
                await ConfigureStartupAsync(arguments, cancellationToken);
                break;
            case "providers":
                PrintProviders();
                break;
            case "players":
                PrintPlayers();
                break;
            case "paths":
                PrintPaths();
                break;
            case "auth":
                Require(arguments, 2, "auth <auth-provider>");
                PrintAuthentication(arguments[1]);
                break;
            case "login":
                Require(arguments, 2, "login <auth-provider> [cookie]");
                await LoginAsync(arguments, cancellationToken);
                break;
            case "logout":
                Require(arguments, 2, "logout <auth-provider>");
                await LogoutAsync(arguments[1], cancellationToken);
                break;
            case "search":
                Require(arguments, 2, "search <keyword> [--source <namespace>]");
                var searchOptions = ParseSearchOptions(arguments);
                await SearchAsync(
                    searchOptions.Keyword,
                    searchOptions.SourceNamespace,
                    cancellationToken);
                break;
            case "metadata":
                Require(arguments, 3, "metadata <track-provider> <track-id> [namespace ...]");
                await PrintMetadataAsync(
                    arguments[1],
                    arguments[2],
                    arguments.Skip(3).ToArray(),
                    cancellationToken);
                break;
            case "enrich":
                Require(arguments, 3, "enrich <track-provider> <track-id>");
                await EnrichAsync(arguments[1], arguments[2], cancellationToken);
                break;
            case "match-playlist":
                Require(arguments, 2, "match-playlist <playlist-id|name> [namespace ...]");
                await MatchPlaylistAsync(
                    ResolvePlaylist(arguments[1]),
                    arguments.Skip(2).ToArray(),
                    cancellationToken);
                break;
            case "lyrics":
                Require(arguments, 3, "lyrics <track-provider> <track-id>");
                await PrintLyricsAsync(arguments[1], arguments[2], cancellationToken);
                break;
            case "sources":
                Require(arguments, 3, "sources <track-provider> <track-id>");
                await PrintSourcesAsync(arguments[1], arguments[2], cancellationToken);
                break;
            case "remote-playlists":
                Require(arguments, 2, "remote-playlists <playlist-provider>");
                await PrintRemotePlaylistsAsync(arguments[1], cancellationToken);
                break;
            case "import":
                Require(arguments, 3, "import <playlist-provider> <playlist-id>");
                await ImportAsync(arguments[1], arguments[2], cancellationToken);
                break;
            case "import-folder":
                Require(arguments, 2, "import-folder <path> [playlist-name]");
                await ImportFolderAsync(
                    arguments[1],
                    arguments.Count > 2 ? string.Join(' ', arguments.Skip(2)) : null,
                    cancellationToken);
                break;
            case "playlists":
                PrintPlaylists();
                break;
            case "playlist":
                if (arguments.Count == 1)
                {
                    PrintPlaylists();
                }
                else
                {
                    PrintPlaylist(ResolvePlaylist(string.Join(' ', arguments.Skip(1))));
                }
                break;
            case "show-playlist":
                Require(arguments, 2, "show-playlist <id>");
                PrintPlaylist(ResolvePlaylist(arguments[1]));
                break;
            case "create-playlist":
                Require(arguments, 2, "create-playlist <name> [--description <text>]");
                await CreatePlaylistAsync(arguments, cancellationToken);
                break;
            case "update-playlist":
                await UpdatePlaylistAsync(arguments, cancellationToken);
                break;
            case "delete-playlist":
                RequireExact(arguments, 2, "delete-playlist <playlist>");
                var deletedPlaylist = ResolvePlaylist(arguments[1]);
                playlists.Delete(deletedPlaylist.Id);
                await library.SaveAsync(cancellationToken);
                _output.WriteLine($"Deleted \"{deletedPlaylist.Name}\" ({deletedPlaylist.Id}).");
                break;
            case "show-track":
                RequireExact(arguments, 3, "show-track <playlist> <item>");
                PrintTrack(ResolvePlaylistItem(ResolvePlaylist(arguments[1]), arguments[2]));
                break;
            case "create-track":
                await CreateTrackAsync(arguments, cancellationToken);
                break;
            case "update-track":
                await UpdateTrackAsync(arguments, cancellationToken);
                break;
            case "move-track":
                RequireExact(arguments, 4, "move-track <playlist> <item> <position>");
                var movedPlaylist = ResolvePlaylist(arguments[1]);
                var movedItem = ResolvePlaylistItem(movedPlaylist, arguments[2]);
                if (!int.TryParse(arguments[3], out var position))
                {
                    throw new ArgumentException("Position must be a positive integer.");
                }
                playlists.Move(movedPlaylist.Id, movedItem.Id, position);
                await library.SaveAsync(cancellationToken);
                _output.WriteLine($"Moved item {movedItem.Id} to position {position}.");
                break;
            case "external-ids":
                await ManageExternalIdsAsync(arguments, cancellationToken);
                break;
            case "add":
                RequireExact(arguments, 4, "add <playlist> <track-provider> <track-id>");
                await AddAsync(arguments[1], arguments[2], arguments[3], cancellationToken);
                break;
            case "remove":
                RequireExact(arguments, 3, "remove <playlist> <item>");
                await RemoveAsync(arguments[1], arguments[2], cancellationToken);
                break;
            case "play":
                Require(arguments, 3, "play <track-provider> <track-id> [--source <source-provider>]");
                await PlayAsync(
                    arguments[1],
                    arguments[2],
                    ParseRequiredSource(arguments),
                    cancellationToken);
                break;
            case "play-playlist":
                Require(arguments, 2, "play-playlist <playlist> [start-number] [--source <source-provider>]");
                var playlistOptions = ParsePlaylistPlaybackOptions(arguments);
                await PlayPlaylistAsync(
                    ResolvePlaylist(arguments[1]),
                    playlistOptions.StartIndex,
                    playlistOptions.RequiredSourceProviderId,
                    cancellationToken);
                break;
            case "play-current":
                var current = await playback.PlayCurrentAsync(cancellationToken);
                _output.WriteLine(current is null
                    ? "The playback queue is empty."
                    : $"Playing: {current.Title ?? current.Id.RemoteId}");
                break;
            case "previous":
            case "prev":
                var previous = await playback.PreviousAsync(cancellationToken);
                _output.WriteLine(previous is null
                    ? "No previous track is available."
                    : $"Previous: {previous.Title ?? previous.Id.RemoteId}");
                break;
            case "next":
                var next = await playback.NextAsync(cancellationToken);
                _output.WriteLine(next is null
                    ? "The end of the queue was reached."
                    : $"Next: {next.Title ?? next.Id.RemoteId}");
                break;
            case "mode":
                Require(arguments, 2, "mode <sequential|repeat|shuffle>");
                var mode = ParsePlaybackMode(arguments[1]);
                await playback.SetModeAsync(mode, cancellationToken);
                _output.WriteLine($"Playback mode: {FormatPlaybackMode(mode)}");
                break;
            case "now-playing":
                PrintNowPlaying();
                break;
            case "pause":
                await playback.PauseAsync(cancellationToken);
                _output.WriteLine("Playback paused.");
                break;
            case "resume":
                await playback.ResumeAsync(cancellationToken);
                _output.WriteLine("Playback resumed.");
                break;
            case "stop":
                await playback.StopAsync(cancellationToken);
                _output.WriteLine("Playback stopped.");
                break;
            case "queue":
                if (arguments.Count == 1)
                {
                    PrintQueue();
                }
                else if (arguments.Count == 2 &&
                         string.Equals(arguments[1], "clear", StringComparison.OrdinalIgnoreCase))
                {
                    await playback.ClearQueueAsync(cancellationToken);
                    _output.WriteLine("Playback queue cleared.");
                }
                else
                {
                    throw new ArgumentException("Usage: queue [clear]");
                }
                break;
            case "sync":
                Require(arguments, 2, "sync <status|pull|push>");
                await SyncAsync(arguments[1], cancellationToken);
                break;
            case "save":
                await library.SaveAsync(cancellationToken);
                await playback.SaveAsync(cancellationToken);
                _output.WriteLine("Library saved.");
                break;
            case "exit":
            case "quit":
                await library.SaveAsync(cancellationToken);
                await playback.SaveAsync(cancellationToken);
                return false;
            default:
                _output.WriteLine($"Unknown command '{arguments[0]}'. Type 'help' for commands.");
                break;
        }

        return true;
    }

    public static IReadOnlyList<string> GetCapabilities(IMusicProvider provider)
    {
        var capabilities = new List<string>();
        if (provider is ISearchProvider) capabilities.Add("Search");
        if (provider is ITrackLookupProvider) capabilities.Add("Track Lookup");
        if (provider is ITrackIdResolverProvider) capabilities.Add("External ID Resolution");
        if (provider is IPlaylistProvider) capabilities.Add("Playlist");
        if (provider is IPlaylistCollectionProvider) capabilities.Add("Playlist Collections");
        if (provider is IPlaybackProvider) capabilities.Add("Playback");
        if (provider is IMetadataProvider) capabilities.Add("Metadata");
        if (provider is ILyricsProvider) capabilities.Add("Lyrics");
        if (provider is IAuthenticationProvider) capabilities.Add("Authentication");
        if (provider is IFolderImportProvider) capabilities.Add("Folder Import");
        return capabilities;
    }

    private void PrintHelp()
    {
        _output.WriteLine("COMMANDS");
        var usageWidth = CommandCatalog.Commands.Max(command => command.Usage.Length) + 2;
        foreach (var command in CommandCatalog.Commands)
        {
            _output.WriteLine($"  {command.Usage.PadRight(usageWidth)}{command.Description}");
        }

        _output.WriteLine();
        _output.WriteLine("PROVIDER PARAMETERS");
        foreach (var role in Enum.GetValues<ProviderParameterRole>())
        {
            _output.WriteLine(
                $"  {ProviderParameterRules.Placeholder(role),-21}" +
                $"{ProviderParameterRules.AllowedValues(providers, role, playbackSources)}");
            _output.WriteLine($"    {ProviderParameterRules.Requirement(role)}.");
        }

        _output.WriteLine();
        _output.WriteLine("KEYS");
        _output.WriteLine("  Tab           Complete the selected command or argument");
        _output.WriteLine("  ↑ / ↓         Browse history when empty; select suggestions while typing");
        _output.WriteLine("  Esc           Close suggestions (Ctrl+Space reopens them)");
        _output.WriteLine("  Home / End    Move to the start or end of the input");
        _output.WriteLine("  Ctrl+U        Delete everything before the cursor");
    }

    private void PrintPlugins()
    {
        _output.WriteLine($"{plugins.Loaded.Count} plugins loaded; {plugins.LastReport.Failures.Count} failed.");
        foreach (var plugin in plugins.Loaded)
        {
            var manifest = plugin.Manifest;
            _output.WriteLine();
            _output.WriteLine($"[Plugin] {manifest.Id} | {manifest.Name} {manifest.Version}");
            _output.WriteLine($"  Entry assembly: {manifest.EntryAssembly}");
            _output.WriteLine($"  Minimum host version: {manifest.MinimumHostVersion}");
            _output.WriteLine($"  Permissions: {string.Join(", ", manifest.Permissions)}");
            foreach (var provider in plugin.Providers)
            {
                _output.WriteLine($"  Provider: {provider.Id} | {provider.Descriptor.Name}");
                _output.WriteLine($"    {provider.Descriptor.Description}");
                _output.WriteLine($"    Capabilities: {string.Join(", ", GetCapabilities(provider))}");
                if (provider is ISearchNamespaceProvider searchProvider)
                {
                    _output.WriteLine($"    Search namespaces: {string.Join(", ", searchProvider.SearchNamespaces)}");
                }
                if (provider is IMetadataProvider metadataProvider)
                {
                    _output.WriteLine($"    Metadata namespaces: {string.Join(", ", metadataProvider.MetadataNamespaces)}");
                }
                if (provider is ITrackIdResolverProvider sourceProvider)
                {
                    _output.WriteLine($"    Accepted IDs: {string.Join(", ", sourceProvider.AcceptedIdSchemes.Select(scheme => $"{scheme.Namespace}:{scheme.Kind ?? "-"}"))}");
                }
            }
            foreach (var player in plugin.Players)
            {
                _output.WriteLine($"  Player: {player.Id} | {player.Descriptor.Name} | priority={player.Priority}");
                _output.WriteLine($"    {player.Descriptor.Description}");
            }
        }
        foreach (var failure in plugins.LastReport.Failures)
        {
            _output.WriteLine($"Plugin load failed ({failure.ManifestPath}): {failure.Error}");
        }
    }

    private void PrintProviders()
    {
        foreach (var provider in providers.Providers)
        {
            _output.WriteLine($"{provider.Id} | {provider.Descriptor.Name}");
            _output.WriteLine($"  {string.Join(", ", GetCapabilities(provider))}");
        }
    }

    private void PrintPlayers()
    {
        foreach (var player in playersRegistry.Players)
        {
            _output.WriteLine($"{player.Id} | {player.Descriptor.Name} | priority {player.Priority}");
            _output.WriteLine($"  {player.Descriptor.Description}");
        }
    }

    private void PrintPaths()
    {
        _output.WriteLine($"Configuration: {appPaths.ConfigurationDirectory}");
        _output.WriteLine($"App settings: {appPaths.AppConfigurationFile}");
        _output.WriteLine($"Data: {appPaths.DataDirectory}");
        _output.WriteLine($"Command history: {history.FilePath}");
        _output.WriteLine($"Cache: {appPaths.CacheDirectory}");
        _output.WriteLine($"Music library: {options.LibraryDirectory}");
        _output.WriteLine($"Plugins: {options.PluginDirectory}");
        _output.WriteLine($"Startup commands: {startup.FilePath}");
    }

    private void PrintAuthentication(string providerId)
    {
        var provider = GetAuthenticationProvider(providerId);
        if (!provider.State.IsAuthenticated)
        {
            _output.WriteLine("Not authenticated.");
            return;
        }

        _output.WriteLine(
            $"Authenticated: {provider.State.AccountName ?? provider.State.UserId ?? providerId} " +
            $"({provider.State.UserId ?? "unknown id"})");
        if (provider.State.AvatarUri is not null)
        {
            _output.WriteLine($"Avatar: {provider.State.AvatarUri}");
        }
    }

    private async Task LoginAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var provider = GetAuthenticationProvider(arguments[1]);
        var methods = await provider.GetAvailableMethodsAsync(cancellationToken);
        if (!methods.Contains(AuthMethod.Cookie))
        {
            throw new ProviderException($"Provider '{arguments[1]}' does not support cookie login.");
        }

        string? cookie;
        if (arguments.Count > 2)
        {
            cookie = string.Join(' ', arguments.Skip(2));
        }
        else
        {
            _output.Write("Paste the complete site cookie: ");
            cookie = await global::System.Console.In.ReadLineAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(cookie))
        {
            throw new ArgumentException("A cookie value is required.");
        }

        var state = await provider.AuthenticateAsync(
            new AuthenticationRequest(
                AuthMethod.Cookie,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["cookie"] = cookie
                }),
            cancellationToken);
        _output.WriteLine(
            $"Authenticated as {state.AccountName ?? state.UserId ?? arguments[1]} " +
            $"({state.UserId ?? "unknown id"}).");
    }

    private async Task LogoutAsync(string providerId, CancellationToken cancellationToken)
    {
        var provider = GetAuthenticationProvider(providerId);
        await provider.SignOutAsync(cancellationToken);
        _output.WriteLine($"Signed out from {providerId}.");
    }

    private IAuthenticationProvider GetAuthenticationProvider(string providerId)
    {
        var provider = ProviderParameterRules.GetRequired(
            providers,
            providerId,
            ProviderParameterRole.Authentication);
        return (IAuthenticationProvider)provider;
    }

    private async Task SearchAsync(
        string keyword,
        string? sourceNamespace,
        CancellationToken cancellationToken)
    {
        var results = await search.SearchAsync(
            keyword,
            providerId: null,
            sourceNamespace: sourceNamespace,
            cancellationToken: cancellationToken);
        var populatedResults = results.Where(result => result.Tracks.Count > 0).ToArray();
        if (populatedResults.Length == 0)
        {
            _output.WriteLine("No tracks found.");
            return;
        }

        foreach (var result in populatedResults)
        {
            _output.WriteLine($"Provider: {providers.Get(result.ProviderId)?.Descriptor.Name ?? result.ProviderId}");
            _output.WriteLine();
            for (var index = 0; index < result.Tracks.Count; index++)
            {
                var track = result.Tracks[index];
                _output.WriteLine($"{index + 1}. {track.Title}");
                _output.WriteLine($"   {string.Join(", ", track.Artists.Select(artist => artist.Name))}");
                _output.WriteLine($"   {track.Id}");
            }
        }
    }

    private static (string Keyword, string? SourceNamespace) ParseSearchOptions(
        IReadOnlyList<string> arguments)
    {
        var keywordParts = new List<string>();
        string? sourceNamespace = null;
        for (var index = 1; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], "--source", StringComparison.OrdinalIgnoreCase))
            {
                keywordParts.Add(arguments[index]);
                continue;
            }

            if (sourceNamespace is not null ||
                index + 1 >= arguments.Count ||
                string.IsNullOrWhiteSpace(arguments[index + 1]) ||
                arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Usage: search <keyword> [--source <namespace>]");
            }

            sourceNamespace = arguments[++index];
        }

        var keyword = string.Join(' ', keywordParts);
        if (string.IsNullOrWhiteSpace(keyword))
        {
            throw new ArgumentException("Usage: search <keyword> [--source <namespace>]");
        }

        return (keyword, sourceNamespace);
    }

    private async Task PrintMetadataAsync(
        string providerId,
        string trackId,
        IReadOnlyList<string> targetNamespaces,
        CancellationToken cancellationToken)
    {
        var track = await GetTrackAsync(providerId, trackId, cancellationToken);
        _output.WriteLine($"Track: {track.Title ?? track.Id.RemoteId} ({track.Id})");
        _output.WriteLine($"Album: {track.Album?.Title}");
        _output.WriteLine($"Artists: {string.Join(", ", track.Artists.Select(artist => artist.Name))}");
        _output.WriteLine();

        var result = await metadata.LookupAsync(
            track,
            targetNamespaces,
            cancellationToken: cancellationToken);
        if (result.Matches.Count == 0)
        {
            _output.WriteLine("No metadata matches found.");
        }

        foreach (var match in result.Matches)
        {
            var value = match.Metadata;
            _output.WriteLine(
                $"{match.SourceNamespace} | {match.Confidence:P0} | {match.MatchLevel} | " +
                $"{value.Title} - {string.Join(", ", value.Artists)}");
            if (!string.IsNullOrWhiteSpace(value.Album))
            {
                _output.WriteLine($"  Album: {value.Album}");
            }

            if (value.Duration is { } duration)
            {
                _output.WriteLine($"  Duration: {duration:mm\\:ss}");
            }

            foreach (var id in value.ExternalIds)
            {
                PrintExternalId(id);
            }
        }

        PrintErrors("Metadata source errors", result.SourceErrors);
    }

    private async Task EnrichAsync(string providerId, string trackId, CancellationToken cancellationToken)
    {
        var track = await GetTrackAsync(providerId, trackId, cancellationToken);
        var (enriched, lookup) = await metadata.EnrichAsync(track, cancellationToken);
        if (ReferenceEquals(enriched, track))
        {
            _output.WriteLine("No high-confidence metadata match was found; the track was not changed.");
            PrintErrors("Metadata source errors", lookup.SourceErrors);
            return;
        }

        _output.WriteLine($"Enriched: {enriched.Title} - {string.Join(", ", enriched.Artists.Select(artist => artist.Name))}");
        _output.WriteLine("This standalone result is transient; use match-playlist to persist metadata in a playlist.");
        _output.WriteLine($"External IDs: {enriched.ExternalIds.Count}");
        foreach (var id in enriched.ExternalIds)
        {
            PrintExternalId(id);
        }

        PrintErrors("Metadata source errors", lookup.SourceErrors);
    }

    private async Task MatchPlaylistAsync(
        Playlist playlist,
        IReadOnlyList<string> targetNamespaces,
        CancellationToken cancellationToken)
    {
        var tracks = playlist.Items
            .Select(item => item.Track)
            .DistinctBy(track => track.Id)
            .ToArray();
        if (tracks.Length == 0)
        {
            _output.WriteLine($"Playlist \"{playlist.Name}\" has no tracks to match.");
            return;
        }

        using var concurrency = new SemaphoreSlim(3, 3);
        var tasks = tracks.Select(async track =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                var result = await metadata.EnrichAsync(track, targetNamespaces, cancellationToken);
                return (Original: track, Track: result.Track, Lookup: result.Lookup);
            }
            finally
            {
                concurrency.Release();
            }
        });
        var results = await Task.WhenAll(tasks);
        var matched = 0;

        foreach (var result in results)
        {
            if (ReferenceEquals(result.Original, result.Track))
            {
                _output.WriteLine($"No reliable match: {result.Original.Title}");
                PrintErrors("  Metadata source errors", result.Lookup.SourceErrors);
                continue;
            }

            foreach (var item in playlist.Items.Where(item => item.Track.Id == result.Original.Id))
            {
                item.Track = result.Track;
            }

            matched++;
            var namespaces = result.Track.ExternalIds
                .Select(id => id.Namespace)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            _output.WriteLine($"Matched: {result.Track.Title} [{string.Join(", ", namespaces)}]");
        }

        if (matched > 0)
        {
            playlist.UpdatedAt = DateTimeOffset.UtcNow;
            library.Touch();
        }

        await library.SaveAsync(cancellationToken);
        _output.WriteLine(
            $"Matched {matched}/{tracks.Length} tracks in \"{playlist.Name}\"" +
            (targetNamespaces.Count == 0
                ? "."
                : $" for {string.Join(", ", targetNamespaces)}."));
    }

    private async Task PrintLyricsAsync(string providerId, string trackId, CancellationToken cancellationToken)
    {
        var track = await GetTrackAsync(providerId, trackId, cancellationToken);
        var result = await lyrics.GetLyricsAsync(track, cancellationToken);
        if (result.Lyrics is null)
        {
            _output.WriteLine("No lyrics found.");
            PrintErrors("Lyrics provider errors", result.ProviderErrors);
            return;
        }

        var value = result.Lyrics;
        _output.WriteLine(
            $"Lyrics: provider={result.ProviderId}, source={value.Source ?? "unknown"}, " +
            $"synchronized={value.IsSynchronized}");
        if (value.SourceId is { } sourceId)
        {
            PrintExternalId(sourceId);
        }

        if (value.Lines is { Count: > 0 } lines)
        {
            foreach (var line in lines)
            {
                var timestamp = line.StartTimeMs is { } milliseconds
                    ? $"[{TimeSpan.FromMilliseconds(milliseconds):mm\\:ss\\.fff}] "
                    : string.Empty;
                _output.WriteLine($"{timestamp}{line.Text}");
                if (line.Translations is { Count: > 0 })
                {
                    _output.WriteLine($"  {string.Join(" / ", line.Translations.Values)}");
                }
                else if (!string.IsNullOrWhiteSpace(line.Romanization))
                {
                    _output.WriteLine($"  {line.Romanization}");
                }
            }
        }
        else
        {
            _output.WriteLine(value.Text);
        }

        PrintErrors("Lyrics provider errors", result.ProviderErrors);
    }

    private async Task PrintSourcesAsync(string providerId, string trackId, CancellationToken cancellationToken)
    {
        var track = await GetTrackAsync(providerId, trackId, cancellationToken);
        var result = await sourceDiscovery.FindAsync(track, cancellationToken: cancellationToken);
        if (result.Candidates.Count == 0)
        {
            _output.WriteLine("No playable source found.");
        }

        foreach (var candidate in result.Candidates)
        {
            var source = candidate.Source switch
            {
                FilePlaybackSource file => $"file:{file.Path}",
                HttpPlaybackSource http => http.Uri.ToString(),
                _ => candidate.Source.GetType().Name
            };
            _output.WriteLine(
                $"{candidate.ProviderId} | {candidate.Confidence:P0} | {candidate.ResolutionMethod} | " +
                $"{candidate.Track.Id} | {source}");
        }

        PrintErrors("Source discovery errors", result.ProviderErrors);
    }

    private void PrintExternalId(ExternalMusicId id) =>
        _output.WriteLine(
            $"  ID: namespace={id.Namespace}, kind={id.Kind ?? "-"}, value={id.Value}");

    private void PrintErrors(string heading, IReadOnlyDictionary<string, string> errors)
    {
        if (errors.Count == 0)
        {
            return;
        }

        _output.WriteLine($"{heading}:");
        foreach (var error in errors)
        {
            _output.WriteLine($"  {error.Key}: {error.Value}");
        }
    }

    private async Task PrintRemotePlaylistsAsync(string providerId, CancellationToken cancellationToken)
    {
        var provider = ProviderParameterRules.GetRequired(
            providers,
            providerId,
            ProviderParameterRole.Playlist);
        var playlistProvider = (IPlaylistProvider)provider;

        if (provider is IPlaylistCollectionProvider collectionProvider)
        {
            await foreach (var collection in collectionProvider.GetUserPlaylistCollectionsAsync(cancellationToken))
            {
                _output.WriteLine($"[{collection.Name}] ({collection.Id})");
                if (collection.Playlists.Count == 0)
                {
                    _output.WriteLine("  (empty)");
                    continue;
                }

                for (var index = 0; index < collection.Playlists.Count; index++)
                {
                    var playlist = collection.Playlists[index];
                    _output.WriteLine(
                        $"  {index + 1}. {playlist.Name} ({playlist.Id}) " +
                        $"[{playlist.TrackCount?.ToString() ?? "unknown"} tracks]");
                }
            }

            return;
        }

        var flatIndex = 1;
        await foreach (var playlist in playlistProvider.GetUserPlaylistsAsync(cancellationToken))
        {
            _output.WriteLine(
                $"{flatIndex++}. {playlist.Name} ({playlist.Id}) " +
                $"[{playlist.TrackCount?.ToString() ?? "unknown"} tracks]");
        }
    }

    private async Task ImportAsync(string providerId, string remoteId, CancellationToken cancellationToken)
    {
        ProviderParameterRules.GetRequired(
            providers,
            providerId,
            ProviderParameterRole.Playlist);
        var playlist = await importer.ImportAsync(providerId, remoteId, cancellationToken);
        await library.SaveAsync(cancellationToken);
        _output.WriteLine($"Imported \"{playlist.Name}\"");
        _output.WriteLine($"{playlist.Items.Count} tracks");
        _output.WriteLine($"Local Playlist ID: {playlist.Id}");
    }

    private async Task ImportFolderAsync(
        string directoryPath,
        string? playlistName,
        CancellationToken cancellationToken)
    {
        var result = await folderImporter.ImportAsync(
            directoryPath,
            playlistName,
            cancellationToken: cancellationToken);
        await library.SaveAsync(cancellationToken);
        _output.WriteLine($"Imported folder \"{directoryPath}\" into \"{result.Playlist.Name}\".");
        _output.WriteLine($"{result.ImportedTracks} tracks imported; {result.SkippedFiles.Count} files skipped.");
        _output.WriteLine($"Local Playlist ID: {result.Playlist.Id}");
    }

    private void PrintPlaylists()
    {
        if (library.Current.Playlists.Count == 0)
        {
            _output.WriteLine("No local playlists.");
            return;
        }

        foreach (var playlist in library.Current.Playlists.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            _output.WriteLine($"{playlist.Id} | {playlist.Name} | {playlist.Items.Count} tracks");
        }
    }

    private void PrintPlaylist(Playlist playlist)
    {
        _output.WriteLine($"{playlist.Name} ({playlist.Id})");
        if (!string.IsNullOrWhiteSpace(playlist.Description))
        {
            _output.WriteLine(playlist.Description);
        }

        var index = 1;
        foreach (var item in playlist.Items
                     .OrderBy(item => FractionalPlaylistOrderingService.Parse(item.SortKey))
                     .ThenBy(item => item.Id))
        {
            var externalIdInitials = string.Concat(item.Track.ExternalIds
                .Where(id => !string.IsNullOrEmpty(id.Namespace))
                .Select(id => id.Namespace[0]));
            _output.WriteLine($"{index++} | {item.Track.Title ?? item.Track.Id.RemoteId} | {item.Track.Id} | {externalIdInitials}");
        }
    }

    private async Task CreatePlaylistAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var fieldStart = 1;
        while (fieldStart < arguments.Count && !arguments[fieldStart].StartsWith("--", StringComparison.Ordinal))
        {
            fieldStart++;
        }
        var name = string.Join(' ', arguments.Skip(1).Take(fieldStart - 1));
        var fields = ParseFields(arguments, fieldStart, "description");
        var playlist = playlists.Create(name, fields.TryGetValue("description", out var description) ? NullIfEmpty(description) : null);
        await library.SaveAsync(cancellationToken);
        _output.WriteLine($"Created \"{playlist.Name}\" ({playlist.Id}).");
    }

    private async Task AddAsync(
        string playlistId,
        string providerId,
        string trackId,
        CancellationToken cancellationToken)
    {
        var playlist = ResolvePlaylist(playlistId);
        var track = await GetTrackAsync(providerId, trackId, cancellationToken);
        var item = playlists.Add(playlist.Id, track);
        await library.SaveAsync(cancellationToken);
        _output.WriteLine($"Added {track.Title} as item {item.Id}.");
    }

    private async Task RemoveAsync(string playlistId, string itemId, CancellationToken cancellationToken)
    {
        var playlist = ResolvePlaylist(playlistId);
        var parsedItemId = ResolvePlaylistItem(playlist, itemId).Id;

        if (!playlists.Remove(playlist.Id, parsedItemId))
        {
            throw new ArgumentException($"Playlist item '{itemId}' was not found.");
        }

        await library.SaveAsync(cancellationToken);
        _output.WriteLine("Playlist item removed.");
    }

    private async Task PlayAsync(
        string providerId,
        string trackId,
        string? requiredSourceProviderId,
        CancellationToken cancellationToken)
    {
        ValidatePlaybackSourceProvider(requiredSourceProviderId);
        var track = await GetTrackAsync(providerId, trackId, cancellationToken);
        await playback.PlayTrackAsync(
            track,
            new PlaybackRequest(RequiredSourceProviderId: requiredSourceProviderId),
            cancellationToken);
    }

    private async Task PlayPlaylistAsync(
        Playlist playlist,
        int startIndex,
        string? requiredSourceProviderId,
        CancellationToken cancellationToken)
    {
        ValidatePlaybackSourceProvider(requiredSourceProviderId);
        var tracks = playlist.Items
            .OrderBy(item => FractionalPlaylistOrderingService.Parse(item.SortKey))
            .ThenBy(item => item.Id)
            .Select(item => item.Track)
            .ToArray();
        await playback.PlayPlaylistAsync(
            tracks,
            startIndex,
            new PlaybackRequest(RequiredSourceProviderId: requiredSourceProviderId),
            cancellationToken);
        _output.WriteLine(
            $"Playing \"{playlist.Name}\" from track {startIndex + 1}/{tracks.Length} " +
            $"in {FormatPlaybackMode(playback.Queue.Mode)} mode.");
    }

    private static string? ParseRequiredSource(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 3)
        {
            return null;
        }

        if (arguments.Count == 5 &&
            string.Equals(arguments[3], "--source", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(arguments[4]))
        {
            return arguments[4];
        }

        throw new ArgumentException(
            "Usage: play <track-provider> <track-id> [--source <source-provider>]");
    }

    private static (int StartIndex, string? RequiredSourceProviderId) ParsePlaylistPlaybackOptions(
        IReadOnlyList<string> arguments)
    {
        var argumentIndex = 2;
        var startNumber = 1;
        if (argumentIndex < arguments.Count &&
            !string.Equals(arguments[argumentIndex], "--source", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(arguments[argumentIndex], out startNumber) || startNumber < 1)
            {
                throw new ArgumentException("Playlist start number must be a positive integer.");
            }

            argumentIndex++;
        }

        string? requiredSourceProviderId = null;
        if (argumentIndex < arguments.Count)
        {
            if (argumentIndex + 2 != arguments.Count ||
                !string.Equals(arguments[argumentIndex], "--source", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(arguments[argumentIndex + 1]))
            {
                throw new ArgumentException(
                    "Usage: play-playlist <playlist> [start-number] [--source <source-provider>]");
            }

            requiredSourceProviderId = arguments[argumentIndex + 1];
        }

        return (startNumber - 1, requiredSourceProviderId);
    }

    private static PlaybackMode ParsePlaybackMode(string value) => value.ToLowerInvariant() switch
    {
        "sequential" or "sequence" => PlaybackMode.Sequential,
        "repeat" or "loop" => PlaybackMode.RepeatAll,
        "shuffle" or "random" => PlaybackMode.Shuffle,
        _ => throw new ArgumentException("Playback mode must be sequential, repeat, or shuffle.")
    };

    private static string FormatPlaybackMode(PlaybackMode mode) => mode switch
    {
        PlaybackMode.Sequential => "sequential",
        PlaybackMode.RepeatAll => "repeat",
        PlaybackMode.Shuffle => "shuffle",
        _ => mode.ToString()
    };

    private void PrintQueue()
    {
        var queue = playback.Queue;
        var items = queue.Items;
        if (items.Count == 0)
        {
            _output.WriteLine("Queue is empty.");
            return;
        }

        _output.WriteLine($"Mode: {FormatPlaybackMode(queue.Mode)}");
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var marker = item.Id == queue.CurrentItem?.Id ? ">" : " ";
            var track = item.Track;
            _output.WriteLine($"{marker} {index + 1}. {track.Title ?? track.Id.RemoteId} ({track.Id})");
        }
    }

    private void PrintNowPlaying()
    {
        var snapshot = playback.Snapshot;
        _output.WriteLine($"State: {snapshot.State}");
        if (snapshot.RestoredStatus is { } restoredStatus)
        {
            _output.WriteLine($"Saved state: {restoredStatus} (use resume or play-current to continue)");
        }
        _output.WriteLine($"Mode: {FormatPlaybackMode(snapshot.Mode)}");
        if (snapshot.CurrentTrack is not { } track)
        {
            _output.WriteLine("No track is selected.");
            return;
        }

        _output.WriteLine($"Track: {track.Title ?? track.Id.RemoteId} ({track.Id})");
        _output.WriteLine($"External IDs: {track.ExternalIds.Count}");
        foreach (var id in track.ExternalIds)
        {
            PrintExternalId(id);
        }

        _output.WriteLine(
            $"Timeline: {FormatTime(snapshot.Timeline.Position)} / " +
            (snapshot.Timeline.Duration is { } duration ? FormatTime(duration) : "unknown"));
        _output.WriteLine(
            $"Queue: {snapshot.CurrentIndex + 1}/{snapshot.QueueCount} | " +
            $"Player: {snapshot.PlayerId ?? "none"} | " +
            $"Source: {snapshot.RequiredSourceProviderId ?? "automatic"}");
    }

    private static string FormatTime(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{(int)value.TotalMinutes}:{value.Seconds:00}";

    private async Task SyncAsync(string action, CancellationToken cancellationToken)
    {
        const string providerId = "mock-git";
        switch (action.ToLowerInvariant())
        {
            case "status":
                var status = await sync.GetStatusAsync(providerId, cancellationToken);
                _output.WriteLine($"Status: {status.State}");
                _output.WriteLine($"Local revision: {status.LocalRevision ?? "n/a"}");
                _output.WriteLine($"Remote revision: {status.RemoteRevision ?? "n/a"}");
                if (status.Message is not null) _output.WriteLine(status.Message);
                break;
            case "pull":
                var pull = await sync.PullAsync(providerId, cancellationToken);
                _output.WriteLine(pull.ProviderResult.Message);
                _output.WriteLine($"Remote revision: {pull.ProviderResult.RemoteRevision ?? "n/a"}");
                _output.WriteLine($"Local revision: {pull.ProviderResult.LocalRevision ?? "n/a"}");
                if (pull.MergeResult is { } merge)
                {
                    _output.WriteLine("Merging...");
                    _output.WriteLine($"+ {merge.AddedPlaylistItems} playlist items");
                    _output.WriteLine($"~ {merge.ChangedPlaylists} playlists");
                    _output.WriteLine($"{merge.Conflicts.Count} conflicts");
                    foreach (var conflict in merge.Conflicts)
                    {
                        _output.WriteLine($"! {conflict.Type}: {conflict.Path}");
                    }

                    _output.WriteLine("Library updated.");
                }

                break;
            case "push":
                var push = await sync.PushAsync(providerId, cancellationToken);
                _output.WriteLine(push.Message);
                break;
            default:
                throw new ArgumentException("Usage: sync <status|pull|push>");
        }
    }

    private Playlist ResolvePlaylist(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Guid.TryParse(value, out var id))
        {
            return playlists.GetRequired(id);
        }

        var matches = library.Current.Playlists
            .Where(playlist => playlist.Id.ToString().StartsWith(value, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(playlist.Name, value, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ArgumentException($"Playlist '{value}' was not found."),
            _ => throw new ArgumentException($"Playlist identifier '{value}' is ambiguous.")
        };
    }

    private async Task<Track> GetTrackAsync(
        string providerId,
        string trackId,
        CancellationToken cancellationToken)
    {
        ProviderParameterRules.GetRequired(
            providers,
            providerId,
            ProviderParameterRole.TrackLookup);
        return await trackLookup.GetTrackAsync(providerId, trackId, cancellationToken)
               ?? throw new ProviderException($"Track '{providerId}:{trackId}' was not found.");
    }

    private void ValidatePlaybackSourceProvider(string? providerId)
    {
        if (providerId is not null)
        {
            ProviderParameterRules.GetRequired(
                providers,
                providerId,
                ProviderParameterRole.PlaybackSource,
                playbackSources);
        }
    }

    private static void Require(IReadOnlyCollection<string> arguments, int count, string usage)
    {
        if (arguments.Count < count)
        {
            throw new ArgumentException($"Usage: {usage}");
        }
    }

    internal static IReadOnlyList<string> Tokenize(string input)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var tokenStarted = false;

        foreach (var character in input)
        {
            if (character == '"')
            {
                quoted = !quoted;
                tokenStarted = true;
            }
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                if (tokenStarted)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }
            }
            else
            {
                current.Append(character);
                tokenStarted = true;
            }
        }

        if (quoted)
        {
            throw new ArgumentException("Unterminated quoted argument.");
        }

        if (tokenStarted)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
