using LemonNeo.Application.Library;
using LemonNeo.Core.Library;
using LemonNeo.Core.Music;
using System.Globalization;

namespace LemonNeo.ConsoleApp;

internal sealed partial class CommandProcessor
{
    private void PrintLibrary()
    {
        var items = library.Current.Playlists.SelectMany(playlist => playlist.Items).ToArray();
        _output.WriteLine($"{library.Current.Playlists.Count} playlists.");
        _output.WriteLine($"{items.Select(item => item.Track.Id).Distinct().Count()} tracks ({items.Length} playlist items).");
    }

    private async Task ConfigureStartupAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (arguments.Count == 1 ||
            (arguments.Count == 2 && arguments[1].Equals("show", StringComparison.OrdinalIgnoreCase)))
        {
            _output.WriteLine($"Startup commands: {startup.FilePath}");
            foreach (var line in await startup.LoadAsync(cancellationToken))
            {
                _output.WriteLine(line);
            }
            return;
        }

        if (arguments.Count == 2 && arguments[1].Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            await startup.WriteAsync(StartupCommands.DefaultScript, cancellationToken);
        }
        else if (arguments.Count == 3 && arguments[1].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            var script = await File.ReadAllTextAsync(Path.GetFullPath(arguments[2]), cancellationToken);
            await startup.WriteAsync(script, cancellationToken);
        }
        else
        {
            throw new ArgumentException("Usage: startup [show|set <file>|reset]");
        }
        await appSettings.SaveAsync(cancellationToken);
        _output.WriteLine($"Startup commands saved to {startup.FilePath}. They run on the next launch.");
    }

    private async Task UpdatePlaylistAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        Require(arguments, 4, "update-playlist <playlist> [--name <name>] [--description <text>]");
        var playlist = ResolvePlaylist(arguments[1]);
        var values = ParseFields(arguments, 2, "name", "description");
        playlists.Update(playlist.Id,
            values.GetValueOrDefault("name", playlist.Name),
            values.TryGetValue("description", out var description) ? NullIfEmpty(description) : playlist.Description);
        await library.SaveAsync(cancellationToken);
        _output.WriteLine($"Updated \"{playlist.Name}\" ({playlist.Id}).");
    }

    private async Task CreateTrackAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        Require(arguments, 3, "create-track <playlist> <title> [--artists <names>] [--album <title>] [--duration <seconds>] [--artwork <uri>]");
        var playlist = ResolvePlaylist(arguments[1]);
        ArgumentException.ThrowIfNullOrWhiteSpace(arguments[2]);
        var track = ApplyTrackFields(new Track
        {
            Id = new MusicItemId("library", Guid.NewGuid().ToString("N")),
            Title = arguments[2]
        }, ParseFields(arguments, 3, TrackFields));
        var item = playlists.Add(playlist.Id, track);
        await library.SaveAsync(cancellationToken);
        _output.WriteLine($"Created {track.Title} as item {item.Id}.");
    }

    private async Task UpdateTrackAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        Require(arguments, 5, "update-track <playlist> <item> --<field> <value> [...]");
        var playlist = ResolvePlaylist(arguments[1]);
        var item = ResolvePlaylistItem(playlist, arguments[2]);
        var track = ApplyTrackFields(item.Track, ParseFields(arguments, 3, TrackFields));
        playlists.UpdateTrack(playlist.Id, item.Id, track);
        await library.SaveAsync(cancellationToken);
        _output.WriteLine($"Updated {track.Title} ({item.Id}).");
    }

    private static readonly string[] TrackFields = ["title", "artists", "album", "duration", "artwork", "provider", "track-id"];

    private static Track ApplyTrackFields(Track track, IReadOnlyDictionary<string, string> fields)
    {
        foreach (var (field, value) in fields)
        {
            switch (field.ToLowerInvariant())
            {
                case "title":
                    ArgumentException.ThrowIfNullOrWhiteSpace(value);
                    track = track with { Title = value };
                    break;
                case "artists":
                    track = track with
                    {
                        Artists = value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                            .Select(name => new ArtistRef { Name = name }).ToArray()
                    };
                    break;
                case "album":
                    track = track with { Album = NullIfEmpty(value) is { } title ? new AlbumRef { Title = title } : null };
                    break;
                case "duration":
                    TimeSpan? duration = null;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                            !double.IsFinite(seconds) || seconds < 0 || seconds >= TimeSpan.MaxValue.TotalSeconds)
                        {
                            throw new ArgumentException("Duration must be a nonnegative number of seconds, or an empty string to clear it.");
                        }
                        duration = TimeSpan.FromSeconds(seconds);
                    }
                    track = track with { Duration = duration };
                    break;
                case "artwork":
                    Uri? artwork = null;
                    if (!string.IsNullOrWhiteSpace(value) && !Uri.TryCreate(value, UriKind.Absolute, out artwork))
                    {
                        throw new ArgumentException("Artwork must be an absolute URI, or an empty string to clear it.");
                    }
                    track = track with { ArtworkUri = artwork };
                    break;
                case "provider":
                    ArgumentException.ThrowIfNullOrWhiteSpace(value);
                    track = track with { Id = track.Id with { ProviderId = value } };
                    break;
                case "track-id":
                    ArgumentException.ThrowIfNullOrWhiteSpace(value);
                    track = track with { Id = track.Id with { RemoteId = value } };
                    break;
            }
        }
        return track;
    }

    private async Task ManageExternalIdsAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        const string usage = "external-ids <playlist> <item> [add|update <namespace> <kind|-> <value> | remove <namespace> <kind|->]";
        Require(arguments, 3, usage);
        var playlist = ResolvePlaylist(arguments[1]);
        var item = ResolvePlaylistItem(playlist, arguments[2]);
        if (arguments.Count == 3)
        {
            var ids = playlists.GetExternalIds(playlist.Id, item.Id);
            _output.WriteLine($"External IDs: {ids.Count}");
            foreach (var id in ids) PrintExternalId(id);
            return;
        }

        var action = arguments[3].ToLowerInvariant();
        RequireExact(arguments, action == "remove" ? 6 : 7, usage);
        var namespaceName = arguments[4];
        var kind = arguments[5] == "-" ? null : arguments[5];
        switch (action)
        {
            case "add":
                playlists.AddExternalId(playlist.Id, item.Id, new ExternalMusicId(namespaceName, arguments[6], kind));
                break;
            case "update":
                playlists.UpdateExternalId(playlist.Id, item.Id, new ExternalMusicId(namespaceName, arguments[6], kind));
                break;
            case "remove":
                if (!playlists.RemoveExternalId(playlist.Id, item.Id, namespaceName, kind))
                {
                    throw new ArgumentException($"External ID scheme '{namespaceName}:{kind ?? "-"}' was not found.");
                }
                break;
            default:
                throw new ArgumentException($"Usage: {usage}");
        }
        await library.SaveAsync(cancellationToken);
        _output.WriteLine($"External ID {action} completed for item {item.Id}.");
    }

    private static PlaylistItem ResolvePlaylistItem(Playlist playlist, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var isNumber = int.TryParse(value, out var number);
        if (isNumber && number >= 1 && number <= playlist.Items.Count)
        {
            var ordered = playlist.Items.OrderBy(item => FractionalPlaylistOrderingService.Parse(item.SortKey))
                .ThenBy(item => item.Id).ToArray();
            return ordered[number - 1];
        }
        var matches = playlist.Items.Where(item =>
            item.Id.ToString().StartsWith(value, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 when isNumber => throw new ArgumentException($"Item number must be between 1 and {playlist.Items.Count}, or match an item ID prefix."),
            0 => throw new ArgumentException($"Playlist item '{value}' was not found."),
            _ => throw new ArgumentException($"Playlist item identifier '{value}' is ambiguous.")
        };
    }

    private void PrintTrack(PlaylistItem item)
    {
        var track = item.Track;
        _output.WriteLine($"Item: {item.Id} | sort key: {item.SortKey}");
        _output.WriteLine($"Track: {track.Title} ({track.Id})");
        _output.WriteLine($"Artists: {string.Join("; ", track.Artists.Select(artist => artist.Name))}");
        _output.WriteLine($"Album: {track.Album?.Title ?? "-"}");
        _output.WriteLine($"Duration: {track.Duration?.TotalSeconds.ToString(CultureInfo.InvariantCulture) ?? "unknown"} seconds");
        _output.WriteLine($"Artwork: {track.ArtworkUri?.ToString() ?? "-"}");
        _output.WriteLine($"External IDs: {track.ExternalIds.Count}");
        foreach (var id in track.ExternalIds) PrintExternalId(id);
    }

    private static Dictionary<string, string> ParseFields(IReadOnlyList<string> arguments, int start, params string[] allowed)
    {
        if ((arguments.Count - start) % 2 != 0)
        {
            throw new ArgumentException("Each field option requires a value. Quote values containing spaces; use \"\" to clear optional fields.");
        }
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = start; index < arguments.Count; index += 2)
        {
            var option = arguments[index];
            if (!option.StartsWith("--", StringComparison.Ordinal) ||
                !allowed.Contains(option[2..], StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Unknown field '{option}'. Allowed: {string.Join(", ", allowed.Select(field => $"--{field}"))}.");
            }
            if (!fields.TryAdd(option[2..], arguments[index + 1]))
            {
                throw new ArgumentException($"Field '{option}' was specified more than once.");
            }
        }
        return fields;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static void RequireExact(IReadOnlyCollection<string> arguments, int count, string usage)
    {
        if (arguments.Count != count) throw new ArgumentException($"Usage: {usage}");
    }
}
