using System.Text;
using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;
using LemonNeo.Plugin.Runtime;

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

        if (string.Equals(commandName, "remove", StringComparison.OrdinalIgnoreCase) && argumentIndex == 2)
        {
            return CompletePlaylistItems(target, hint);
        }

        if (TryGetProviderFilter(commandName, target, out var providerFilter))
        {
            return CompleteProviders(target, hint, providerFilter);
        }

        if (string.Equals(commandName, "mode", StringComparison.OrdinalIgnoreCase) && argumentIndex == 1)
        {
            return CompleteValues(target, hint, "Playback modes",
                ("sequential", "Play in queue order"),
                ("repeat", "Repeat the queue"),
                ("shuffle", "Randomized queue order"));
        }

        if (string.Equals(commandName, "sync", StringComparison.OrdinalIgnoreCase) && argumentIndex == 1)
        {
            return CompleteValues(target, hint, "Sync actions",
                ("status", "Compare local and remote revisions"),
                ("pull", "Fetch and merge the remote library"),
                ("push", "Publish the local library"));
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
            return CommandCompletion.Empty(target.Start, "Start typing a command · Tab completes · ↑↓ selects");
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
        Func<IMusicProvider, bool> filter)
    {
        var suggestions = providers.Providers
            .Where(filter)
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
            "Providers",
            hint,
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
            "playlist" or "show-playlist" or "match-playlist" or "add" or "remove" or "play-playlist";

    private static bool IsNamespaceArgument(string command, int argumentIndex) =>
        (string.Equals(command, "metadata", StringComparison.OrdinalIgnoreCase) && argumentIndex >= 3) ||
        (string.Equals(command, "match-playlist", StringComparison.OrdinalIgnoreCase) && argumentIndex >= 2);

    private static bool IsSourceOptionPosition(string command, CompletionTarget target)
    {
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

    private static bool TryGetProviderFilter(
        string command,
        CompletionTarget target,
        out Func<IMusicProvider, bool> filter)
    {
        filter = _ => true;
        var argumentIndex = target.TokenIndex;

        if (target.Tokens.Count > 1 &&
            string.Equals(target.Tokens[Math.Max(0, argumentIndex - 1)], "--source", StringComparison.OrdinalIgnoreCase))
        {
            filter = provider => provider is IPlaybackProvider;
            return true;
        }

        if (argumentIndex == 1 && command.ToLowerInvariant() is "auth" or "login" or "logout")
        {
            filter = provider => provider is IAuthenticationProvider;
            return true;
        }

        if (argumentIndex == 1 && command.ToLowerInvariant() is "remote-playlists" or "import")
        {
            filter = provider => provider is IPlaylistProvider;
            return true;
        }

        if (argumentIndex == 1 && command.ToLowerInvariant() is
            "metadata" or "enrich" or "lyrics" or "sources" or "play")
        {
            return true;
        }

        if (argumentIndex == 2 && string.Equals(command, "add", StringComparison.OrdinalIgnoreCase))
        {
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
