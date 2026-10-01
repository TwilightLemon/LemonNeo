using System.Text;
using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Playback;
using LemonNeo.Plugin.Abstractions;
using LemonNeo.Plugin.Runtime;

namespace LemonNeo.ConsoleApp;

internal sealed class CommandProcessor(
    PluginLoader plugins,
    IProviderRegistry providers,
    IPlayerRegistry playersRegistry,
    LibraryService library,
    SearchService search,
    MetadataService metadata,
    LyricsService lyrics,
    PlaylistService playlists,
    ImportService importer,
    FolderImportService folderImporter,
    PlaybackResolver playback,
    PlaybackSourceDiscoveryService sourceDiscovery,
    IPlaybackQueue queue,
    SyncService sync)
{
    private readonly TextWriter _output = global::System.Console.Out;

    public async Task RunInteractiveAsync(CancellationToken cancellationToken)
    {
        _output.WriteLine("Type 'help' for commands.");
        while (!cancellationToken.IsCancellationRequested)
        {
            _output.Write("> ");
            var line = await global::System.Console.In.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                await library.SaveAsync(cancellationToken);
                return;
            }

            try
            {
                if (!await ExecuteAsync(line, cancellationToken))
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is LemonNeoException or ArgumentException or InvalidOperationException)
            {
                _output.WriteLine($"Error: {exception.Message}");
            }
        }
    }

    public async Task RunDemoAsync(CancellationToken cancellationToken)
    {
        _output.WriteLine("Running phase-one demonstration...");
        await ExecuteAsync("search Blinding Lights", cancellationToken);
        await ExecuteAsync("remote-playlists mock", cancellationToken);
        var imported = await importer.ImportAsync("mock", "coding", cancellationToken);
        await library.SaveAsync(cancellationToken);
        _output.WriteLine($"Imported \"{imported.Name}\"");
        _output.WriteLine($"{imported.Items.Count} tracks");
        _output.WriteLine($"Local Playlist ID: {imported.Id}");

        var local = playlists.Create("Demo Local Playlist");
        var track = await GetTrackAsync("mock", "track-001", cancellationToken);
        library.UpsertTrack(track);
        var item = playlists.Add(local.Id, track.ToReference());
        playlists.Remove(local.Id, item.Id);
        await library.SaveAsync(cancellationToken);
        _output.WriteLine($"Created local playlist, added and removed item {item.Id}.");

        var persistedPlaylistCount = library.Current.Playlists.Count;
        await library.LoadAsync(cancellationToken);
        _output.WriteLine($"Reloaded {library.Current.Playlists.Count} playlists from JSON (expected {persistedPlaylistCount}).");
        try
        {
            await playback.PlayAsync(
                track.ToReference(),
                new PlaybackRequest(RequiredSourceProviderId: "mock"),
                cancellationToken);
        }
        catch (PlaybackResolutionException exception)
        {
            _output.WriteLine($"Expected unavailable mock source: {exception.Message}");
        }
        await ExecuteAsync("sync status", cancellationToken);
        await ExecuteAsync("sync pull", cancellationToken);
        await ExecuteAsync("sync push", cancellationToken);
        _output.WriteLine("Phase-one demonstration completed.");
    }

    public async Task<bool> ExecuteAsync(string line, CancellationToken cancellationToken)
    {
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
            case "providers":
                PrintProviders();
                break;
            case "players":
                PrintPlayers();
                break;
            case "search":
                Require(arguments, 2, "search <keyword>");
                await SearchAsync(string.Join(' ', arguments.Skip(1)), cancellationToken);
                break;
            case "metadata":
                Require(arguments, 3, "metadata <provider> <track-id> [namespace ...]");
                await PrintMetadataAsync(
                    arguments[1],
                    arguments[2],
                    arguments.Skip(3).ToArray(),
                    cancellationToken);
                break;
            case "enrich":
                Require(arguments, 3, "enrich <provider> <track-id>");
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
                Require(arguments, 3, "lyrics <provider> <track-id>");
                await PrintLyricsAsync(arguments[1], arguments[2], cancellationToken);
                break;
            case "sources":
                Require(arguments, 3, "sources <provider> <track-id>");
                await PrintSourcesAsync(arguments[1], arguments[2], cancellationToken);
                break;
            case "remote-playlists":
                Require(arguments, 2, "remote-playlists <provider>");
                await PrintRemotePlaylistsAsync(arguments[1], cancellationToken);
                break;
            case "import":
                Require(arguments, 3, "import <provider> <playlist-id>");
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
            case "show-playlist":
                Require(arguments, 2, "show-playlist <id>");
                PrintPlaylist(ResolvePlaylist(arguments[1]));
                break;
            case "create-playlist":
                Require(arguments, 2, "create-playlist <name>");
                await CreatePlaylistAsync(string.Join(' ', arguments.Skip(1)), cancellationToken);
                break;
            case "add":
                Require(arguments, 4, "add <playlist-id> <provider> <track-id>");
                await AddAsync(arguments[1], arguments[2], arguments[3], cancellationToken);
                break;
            case "remove":
                Require(arguments, 3, "remove <playlist-id> <item-id>");
                await RemoveAsync(arguments[1], arguments[2], cancellationToken);
                break;
            case "play":
                Require(arguments, 3, "play <provider> <track-id> [--source <provider>]");
                await PlayAsync(
                    arguments[1],
                    arguments[2],
                    ParseRequiredSource(arguments),
                    cancellationToken);
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
                PrintQueue();
                break;
            case "sync":
                Require(arguments, 2, "sync <status|pull|push>");
                await SyncAsync(arguments[1], cancellationToken);
                break;
            case "save":
                await library.SaveAsync(cancellationToken);
                _output.WriteLine("Library saved.");
                break;
            case "exit":
            case "quit":
                await library.SaveAsync(cancellationToken);
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
        if (provider is IPlaylistProvider) capabilities.Add("Playlist");
        if (provider is IPlaybackProvider) capabilities.Add("Playback");
        if (provider is IMetadataProvider) capabilities.Add("Metadata");
        if (provider is ILyricsProvider) capabilities.Add("Lyrics");
        if (provider is IAuthenticationProvider) capabilities.Add("Authentication");
        if (provider is IFolderImportProvider) capabilities.Add("Folder Import");
        return capabilities;
    }

    private void PrintHelp()
    {
        _output.WriteLine(
            """
            help
            plugins
            providers
            players
            search <keyword>
            metadata <provider> <track-id> [namespace ...]
            enrich <provider> <track-id>
            match-playlist <playlist-id|name> [namespace ...]
            lyrics <provider> <track-id>
            sources <provider> <track-id>
            remote-playlists <provider>
            import <provider> <playlist-id>
            import-folder <path> [playlist-name]
            playlists
            show-playlist <id>
            create-playlist <name>
            add <playlist-id> <provider> <track-id>
            remove <playlist-id> <item-id>
            play <provider> <track-id> [--source <provider>]
            pause
            resume
            stop
            queue
            sync status
            sync pull
            sync push
            save
            exit
            """);
    }

    private void PrintPlugins()
    {
        foreach (var plugin in plugins.Loaded)
        {
            _output.WriteLine($"{plugin.Manifest.Id} | {plugin.Manifest.Name} {plugin.Manifest.Version}");
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

    private async Task SearchAsync(string keyword, CancellationToken cancellationToken)
    {
        var results = await search.SearchAsync(keyword, cancellationToken: cancellationToken);
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

    private async Task PrintMetadataAsync(
        string providerId,
        string trackId,
        IReadOnlyList<string> targetNamespaces,
        CancellationToken cancellationToken)
    {
        var track = await GetTrackAsync(providerId, trackId, cancellationToken);
        var result = await metadata.LookupAsync(
            track.ToReference(),
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

        library.UpsertTrack(enriched);
        await library.SaveAsync(cancellationToken);
        _output.WriteLine($"Enriched: {enriched.Title} - {string.Join(", ", enriched.Artists.Select(artist => artist.Name))}");
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
            .Select(item => library.FindTrack(item.Track.Id))
            .OfType<Track>()
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

            library.UpsertTrack(result.Track);
            matched++;
            var namespaces = result.Track.ExternalIds
                .Select(id => id.Namespace)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            _output.WriteLine($"Matched: {result.Track.Title} [{string.Join(", ", namespaces)}]");
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
        var result = await lyrics.GetLyricsAsync(track.ToReference(), cancellationToken);
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
        var result = await sourceDiscovery.FindAsync(track.ToReference(), cancellationToken: cancellationToken);
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
            $"  ID: namespace={id.Namespace}, authority={id.Authority ?? "-"}, " +
            $"kind={id.Kind ?? "-"}, value={id.Value}");

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
        var provider = providers.Get(providerId)
            ?? throw new ProviderException($"Provider '{providerId}' was not found.");
        if (provider is not IPlaylistProvider playlistProvider)
        {
            throw new ProviderException($"Provider '{providerId}' does not support playlists.");
        }

        var index = 1;
        await foreach (var playlist in playlistProvider.GetUserPlaylistsAsync(cancellationToken))
        {
            _output.WriteLine($"{index++}. {playlist.Name} ({playlist.Id}) [{playlist.TrackCount ?? 0} tracks]");
        }
    }

    private async Task ImportAsync(string providerId, string remoteId, CancellationToken cancellationToken)
    {
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

        foreach (var item in playlist.Items
                     .OrderBy(item => FractionalPlaylistOrderingService.Parse(item.SortKey))
                     .ThenBy(item => item.Id))
        {
            _output.WriteLine($"{item.Id} | {item.SortKey} | {item.Track.Title ?? item.Track.Id.RemoteId} | {item.Track.Id}");
        }
    }

    private async Task CreatePlaylistAsync(string name, CancellationToken cancellationToken)
    {
        var playlist = playlists.Create(name);
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
        library.UpsertTrack(track);
        var item = playlists.Add(playlist.Id, track.ToReference());
        await library.SaveAsync(cancellationToken);
        _output.WriteLine($"Added {track.Title} as item {item.Id}.");
    }

    private async Task RemoveAsync(string playlistId, string itemId, CancellationToken cancellationToken)
    {
        var playlist = ResolvePlaylist(playlistId);
        if (!Guid.TryParse(itemId, out var parsedItemId))
        {
            throw new ArgumentException($"'{itemId}' is not a valid item id.");
        }

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
        var track = library.FindTrack(new MusicItemId(providerId, trackId))
            ?? await GetTrackAsync(providerId, trackId, cancellationToken);
        await playback.PlayAsync(
            track.ToReference(),
            new PlaybackRequest(RequiredSourceProviderId: requiredSourceProviderId),
            cancellationToken);
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

        throw new ArgumentException("Usage: play <provider> <track-id> [--source <provider>]");
    }

    private void PrintQueue()
    {
        if (queue.Items.Count == 0)
        {
            _output.WriteLine("Queue is empty.");
            return;
        }

        for (var index = 0; index < queue.Items.Count; index++)
        {
            var track = queue.Items[index];
            _output.WriteLine($"{index + 1}. {track.Title ?? track.Id.RemoteId} ({track.Id})");
        }
    }

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
        CancellationToken cancellationToken) =>
        library.FindTrack(new MusicItemId(providerId, trackId))
        ?? await search.FindTrackAsync(providerId, trackId, cancellationToken)
        ?? throw new ProviderException($"Track '{providerId}:{trackId}' was not found.");

    private static void Require(IReadOnlyCollection<string> arguments, int count, string usage)
    {
        if (arguments.Count < count)
        {
            throw new ArgumentException($"Usage: {usage}");
        }
    }

    private static IReadOnlyList<string> Tokenize(string input)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        foreach (var character in input)
        {
            if (character == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (quoted)
        {
            throw new ArgumentException("Unterminated quoted argument.");
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
