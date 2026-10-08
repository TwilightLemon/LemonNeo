using LemonNeo.Abstractions.Providers;
using LemonNeo.Application.Library;
using LemonNeo.Core.Library;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using System.Text;

namespace LemonNeo.ConsoleApp;

internal sealed record CommandSuggestion(
    string InsertText,
    string DisplayText,
    string Description);

internal sealed record CommandCompletion(
    int ReplaceStart,
    int ReplaceLength,
    IReadOnlyList<CommandSuggestion> Suggestions,
    string? Title = null,
    string? Hint = null,
    bool PreferSelection = false,
    bool CurrentValueIsExact = false)
{
    public static CommandCompletion Empty(int cursor, string? hint = null) =>
        new(cursor, 0, [], Hint: hint);
}

internal sealed class CommandCompletionService(
    IProviderRegistry providers,
    IPlaybackSourceProviderRegistry playbackSources,
    LibraryService library)
{
    private static readonly string[] MetadataNamespaces =
    [
        MusicIdNamespaces.NeteaseMusic,
        MusicIdNamespaces.QQMusic,
        MusicIdNamespaces.KugouMusic,
        MusicIdNamespaces.AppleMusic,
        MusicIdNamespaces.Spotify,
        MusicIdNamespaces.Musixmatch,
        MusicIdNamespaces.SodaMusic,
        MusicIdNamespaces.LrcLib,
        MusicIdNamespaces.Isrc,
        MusicIdNamespaces.File
    ];
    public CommandCompletion Complete(string input, int cursor)
    {
        var target = CompletionTarget.Parse(input, cursor);
        if (target.TokenIndex == 0 &&
            cursor == input.Length &&
            string.Equals(target.Value, "playlist", StringComparison.OrdinalIgnoreCase))
        {
            var playlistDescriptor = CommandCatalog.Find("playlist")!;
            return CompletePlaylists(
                new CompletionTarget(cursor, 0, 1, string.Empty, [target.Value, string.Empty]),
                $"{playlistDescriptor.Usage}  ·  {playlistDescriptor.Description}");
        }

        if (target.TokenIndex == 0)
        {
            return CompleteCommand(target);
        }

        var commandName = target.Tokens[0];
        var descriptor = CommandCatalog.Find(commandName);
        if (descriptor is null)
        {
            return CommandCompletion.Empty(target.Start, "Unknown command — edit it or press Tab to complete.");
        }

        var hint = $"{descriptor.Usage}  ·  {descriptor.Description}";
        var argumentIndex = target.TokenIndex;

        if (IsPlaylistArgument(commandName, argumentIndex))
        {
            return CompletePlaylists(target, hint);
        }

        if (argumentIndex == 2 && commandName.ToLowerInvariant() is
            "remove" or "show-track" or "update-track" or "move-track" or "external-ids")
        {
            return CompletePlaylistItems(target, hint);
        }

        if (commandName.Equals("external-ids", StringComparison.OrdinalIgnoreCase) && argumentIndex == 3)
        {
            return CompleteValues(target, hint, "External ID actions",
                ("add", "Add a new ID scheme"), ("update", "Change an existing ID value"), ("remove", "Delete an ID scheme"));
        }

        if (commandName.Equals("startup", StringComparison.OrdinalIgnoreCase) && argumentIndex == 1)
        {
            return CompleteValues(target, hint, "Startup commands",
                ("show", "Show the startup script"), ("set", "Copy a script from a file"), ("reset", "Restore the default script"));
        }

        var fieldStart = commandName.ToLowerInvariant() switch
        {
            "update-playlist" => 2,
            "create-track" or "update-track" => 3,
            _ => -1
        };
        if (commandName.Equals("create-playlist", StringComparison.OrdinalIgnoreCase) && argumentIndex >= 2 &&
            !target.Tokens.Contains("--description", StringComparer.OrdinalIgnoreCase))
        {
            return CompleteValues(target, hint, "Fields", ("--description", "Set the playlist description"));
        }
        if (fieldStart >= 0 && argumentIndex >= fieldStart && (argumentIndex - fieldStart) % 2 == 0)
        {
            var fields = fieldStart == 2
                ? new[] { "name", "description" }
                : new[] { "title", "artists", "album", "duration", "artwork", "provider", "track-id" };
            return CompleteValues(target, hint, "Fields",
                fields.Where(field => !target.Tokens.Contains($"--{field}", StringComparer.OrdinalIgnoreCase))
                    .Select(field => ($"--{field}", $"Set {field}")).ToArray());
        }

        if (IsSearchSourceArgument(commandName, target))
        {
            var searchNamespaces = providers.Providers
                .OfType<ISearchNamespaceProvider>()
                .SelectMany(provider => provider.SearchNamespaces)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(value => (value, "Catalog source namespace"))
                .ToArray();
            return CompleteValues(
                target,
                hint,
                "Search source namespaces",
                searchNamespaces);
        }

        if (TryGetProviderRole(commandName, target, out var providerRole))
        {
            return CompleteProviders(target, hint, providerRole);
        }

        if (string.Equals(commandName, "mode", StringComparison.OrdinalIgnoreCase) && argumentIndex == 1)
        {
            return CompleteValues(target, hint, "Playback modes",
                ("sequential", "Play in queue order"),
                ("repeat", "Repeat the queue"),
                ("shuffle", "Randomized queue order"));
        }

        if (string.Equals(commandName, "queue", StringComparison.OrdinalIgnoreCase) && argumentIndex == 1)
        {
            return CompleteValues(target, hint, "Queue actions", ("clear", "Remove every queued track"));
        }

        if (IsNamespaceArgument(commandName, argumentIndex))
        {
            return CompleteValues(
                target,
                hint,
                "Metadata namespaces",
                MetadataNamespaces.Select(value => (value, "Metadata source namespace")).ToArray());
        }

        if (IsSourceOptionPosition(commandName, target))
        {
            return CompleteValues(target, hint, "Options", ("--source", "Require a playback source provider"));
        }

        return new CommandCompletion(target.Start, target.Length, [], Hint: hint);
    }

    private static CommandCompletion CompleteCommand(CompletionTarget target)
    {
        if (string.IsNullOrEmpty(target.Value))
        {
            return CommandCompletion.Empty(
                target.Start,
                "↑↓ history · start typing for suggestions · Tab completes");
        }

        var matches = Rank(
                CommandCatalog.Commands.SelectMany(command =>
                    new[] { command.Name }.Concat(command.Aliases)
                        .Select(name => new CommandSuggestion(name, name, command.Description))),
                target.Value,
                suggestion => suggestion.DisplayText)
            .ToArray();
        var exact = matches.Any(item =>
            string.Equals(item.InsertText, target.Value, StringComparison.OrdinalIgnoreCase));
        return new CommandCompletion(
            target.Start,
            target.Length,
            matches,
            "Commands",
            matches.FirstOrDefault() is { } first
                ? CommandCatalog.Find(first.InsertText)?.Usage
                : "No matching command",
            CurrentValueIsExact: exact);
    }

    private CommandCompletion CompletePlaylists(CompletionTarget target, string hint)
    {
        var nameCounts = library.Current.Playlists
            .GroupBy(playlist => playlist.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var suggestions = library.Current.Playlists.Select(playlist =>
        {
            var useName = nameCounts[playlist.Name] == 1 && !playlist.Name.Contains('"');
            var value = useName ? Quote(playlist.Name) : playlist.Id.ToString();
            return new CommandSuggestion(
                value,
                playlist.Name,
                $"{playlist.Items.Count} tracks  ·  {playlist.Id.ToString()[..8]}");
        });
        var matches = Rank(suggestions, target.Value, suggestion =>
                $"{suggestion.DisplayText} {suggestion.Description}")
            .ToArray();
        var exact = target.Value.Length > 0 && library.Current.Playlists.Count(playlist =>
            string.Equals(playlist.Name, target.Value, StringComparison.OrdinalIgnoreCase) ||
            playlist.Id.ToString().StartsWith(target.Value, StringComparison.OrdinalIgnoreCase)) == 1;

        return new CommandCompletion(
            target.Start,
            target.Length,
            matches,
            "Local playlists",
            library.Current.Playlists.Count == 0 ? "No local playlists yet." : hint,
            PreferSelection: true,
            CurrentValueIsExact: exact);
    }

    private CommandCompletion CompletePlaylistItems(CompletionTarget target, string hint)
    {
        var playlist = target.Tokens.Count > 1
            ? TryResolvePlaylist(target.Tokens[1])
            : null;
        if (playlist is null)
        {
            return new CommandCompletion(
                target.Start,
                target.Length,
                [],
                Hint: "Select a playlist before choosing an item.");
        }

        var suggestions = playlist.Items.Select(item => new CommandSuggestion(
            item.Id.ToString(),
            item.Track.Title ?? item.Track.Id.RemoteId,
            $"{string.Join(", ", item.Track.Artists.Select(artist => artist.Name))}  ·  {item.Id.ToString()[..8]}"));
        var matches = Rank(suggestions, target.Value, suggestion =>
                $"{suggestion.DisplayText} {suggestion.Description} {suggestion.InsertText}")
            .ToArray();
        return new CommandCompletion(
            target.Start,
            target.Length,
            matches,
            $"Items in {playlist.Name}",
            hint,
            PreferSelection: true,
            CurrentValueIsExact: target.Value.Length > 0 && playlist.Items.Count(item =>
                item.Id.ToString().StartsWith(target.Value, StringComparison.OrdinalIgnoreCase)) == 1);
    }

    private CommandCompletion CompleteProviders(
        CompletionTarget target,
        string hint,
        ProviderParameterRole role)
    {
        var allowed = ProviderParameterRules.Allowed(providers, role, playbackSources);
        var suggestions = allowed
            .Select(provider => new CommandSuggestion(
                provider.Id,
                provider.Id,
                provider.Descriptor.Name));
        var matches = Rank(suggestions, target.Value, suggestion =>
                $"{suggestion.DisplayText} {suggestion.Description}")
            .ToArray();
        return new CommandCompletion(
            target.Start,
            target.Length,
            matches,
            ProviderParameterRules.Title(role),
            $"{hint}  ·  Allowed: {ProviderParameterRules.AllowedValues(providers, role, playbackSources)}",
            CurrentValueIsExact: matches.Any(item =>
                string.Equals(item.InsertText, target.Value, StringComparison.OrdinalIgnoreCase)));
    }

    private static CommandCompletion CompleteValues(
        CompletionTarget target,
        string hint,
        string title,
        params (string Value, string Description)[] values)
    {
        var suggestions = values.Select(value =>
            new CommandSuggestion(value.Value, value.Value, value.Description));
        var matches = Rank(suggestions, target.Value, suggestion => suggestion.DisplayText).ToArray();
        return new CommandCompletion(
            target.Start,
            target.Length,
            matches,
            title,
            hint,
            CurrentValueIsExact: matches.Any(item =>
                string.Equals(item.InsertText, target.Value, StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<CommandSuggestion> Rank(
        IEnumerable<CommandSuggestion> suggestions,
        string query,
        Func<CommandSuggestion, string> searchText) =>
        suggestions
            .Select(suggestion => (Suggestion: suggestion, Rank: MatchRank(searchText(suggestion), query)))
            .Where(item => item.Rank >= 0)
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Suggestion.DisplayText, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Suggestion);

    private static int MatchRank(string value, string query)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        if (value.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return value.Contains(query, StringComparison.OrdinalIgnoreCase) ? 1 : -1;
    }

    private static bool IsPlaylistArgument(string command, int argumentIndex) =>
        argumentIndex == 1 && command.ToLowerInvariant() is
            "playlist" or "show-playlist" or "match-playlist" or "add" or "remove" or "play-playlist" or
            "update-playlist" or "delete-playlist" or "show-track" or "create-track" or "update-track" or "move-track" or "external-ids";

    private static bool IsNamespaceArgument(string command, int argumentIndex) =>
        (string.Equals(command, "metadata", StringComparison.OrdinalIgnoreCase) && argumentIndex >= 3) ||
        (string.Equals(command, "match-playlist", StringComparison.OrdinalIgnoreCase) && argumentIndex >= 2);

    private static bool IsSearchSourceArgument(string command, CompletionTarget target) =>
        string.Equals(command, "search", StringComparison.OrdinalIgnoreCase) &&
        target.TokenIndex > 1 &&
        string.Equals(
            target.Tokens[target.TokenIndex - 1],
            "--source",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSourceOptionPosition(string command, CompletionTarget target)
    {
        if (string.Equals(command, "search", StringComparison.OrdinalIgnoreCase))
        {
            return target.TokenIndex >= 2 &&
                   !target.Tokens.Contains("--source", StringComparer.OrdinalIgnoreCase) &&
                   (target.Value.Length == 0 || target.Value.StartsWith("-", StringComparison.Ordinal));
        }

        if (string.Equals(command, "play", StringComparison.OrdinalIgnoreCase))
        {
            return target.TokenIndex == 3;
        }

        if (!string.Equals(command, "play-playlist", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return target.TokenIndex is 2 or 3 &&
               (target.Value.Length == 0 || target.Value.StartsWith("-", StringComparison.Ordinal));
    }

    private static bool TryGetProviderRole(
        string command,
        CompletionTarget target,
        out ProviderParameterRole role)
    {
        role = default;
        var argumentIndex = target.TokenIndex;

        if (command.ToLowerInvariant() is "play" or "play-playlist" &&
            target.Tokens.Count > 1 &&
            string.Equals(target.Tokens[Math.Max(0, argumentIndex - 1)], "--source", StringComparison.OrdinalIgnoreCase))
        {
            role = ProviderParameterRole.PlaybackSource;
            return true;
        }

        if (argumentIndex == 1 && command.ToLowerInvariant() is "auth" or "login" or "logout")
        {
            role = ProviderParameterRole.Authentication;
            return true;
        }

        if (argumentIndex == 1 && command.ToLowerInvariant() is "remote-playlists" or "import")
        {
            role = ProviderParameterRole.Playlist;
            return true;
        }

        if (argumentIndex == 1 && command.ToLowerInvariant() is
            "metadata" or "enrich" or "lyrics" or "sources" or "play")
        {
            role = ProviderParameterRole.TrackLookup;
            return true;
        }

        if (argumentIndex == 2 && string.Equals(command, "add", StringComparison.OrdinalIgnoreCase))
        {
            role = ProviderParameterRole.TrackLookup;
            return true;
        }

        return false;
    }

    private Playlist? TryResolvePlaylist(string value)
    {
        var matches = library.Current.Playlists.Where(playlist =>
                playlist.Id.ToString().StartsWith(value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(playlist.Name, value, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string Quote(string value) =>
        value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;

    private sealed record CompletionTarget(
        int Start,
        int Length,
        int TokenIndex,
        string Value,
        IReadOnlyList<string> Tokens)
    {
        public static CompletionTarget Parse(string input, int cursor)
        {
            cursor = Math.Clamp(cursor, 0, input.Length);
            var tokens = new List<string>();
            var starts = new List<int>();
            var index = 0;

            while (index < cursor)
            {
                while (index < cursor && char.IsWhiteSpace(input[index]))
                {
                    index++;
                }

                if (index >= cursor)
                {
                    break;
                }

                starts.Add(index);
                var value = new StringBuilder();
                var quoted = false;
                while (index < cursor)
                {
                    var character = input[index];
                    if (character == '"')
                    {
                        quoted = !quoted;
                        index++;
                    }
                    else if (char.IsWhiteSpace(character) && !quoted)
                    {
                        break;
                    }
                    else
                    {
                        value.Append(character);
                        index++;
                    }
                }

                tokens.Add(value.ToString());
            }

            var startsNewToken = cursor == 0 || char.IsWhiteSpace(input[cursor - 1]);
            if (startsNewToken)
            {
                tokens.Add(string.Empty);
                starts.Add(cursor);
            }

            if (tokens.Count == 0)
            {
                tokens.Add(string.Empty);
                starts.Add(cursor);
            }

            var tokenIndex = tokens.Count - 1;
            var start = starts[tokenIndex];
            var end = FindTokenEnd(input, start);
            return new CompletionTarget(
                start,
                end - start,
                tokenIndex,
                tokens[tokenIndex],
                tokens);
        }

        private static int FindTokenEnd(string input, int start)
        {
            var quoted = false;
            var index = start;
            while (index < input.Length)
            {
                var character = input[index];
                if (character == '"')
                {
                    quoted = !quoted;
                }
                else if (char.IsWhiteSpace(character) && !quoted)
                {
                    break;
                }

                index++;
            }

            return index;
        }
    }
}
