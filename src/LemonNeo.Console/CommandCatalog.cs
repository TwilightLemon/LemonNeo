namespace LemonNeo.ConsoleApp;

internal sealed record CommandDescriptor(
    string Name,
    string Usage,
    string Description,
    params string[] Aliases)
{
    public bool Matches(string value) =>
        string.Equals(Name, value, StringComparison.OrdinalIgnoreCase) ||
        Aliases.Contains(value, StringComparer.OrdinalIgnoreCase);
}

internal static class CommandCatalog
{
    public static IReadOnlyList<CommandDescriptor> Commands { get; } =
    [
        new("help", "help", "Show commands and keyboard shortcuts."),
        new("plugins", "plugins", "List loaded plugins."),
        new("providers", "providers", "List music providers and capabilities."),
        new("players", "players", "List playback engines."),
        new("paths", "paths", "Show configuration and library paths."),
        new("auth", "auth <provider>", "Show provider authentication state."),
        new("login", "login <provider> [cookie]", "Sign in to a provider."),
        new("logout", "logout <provider>", "Sign out from a provider."),
        new("search", "search <keyword>", "Search tracks across providers."),
        new("metadata", "metadata <provider> <track-id> [namespace ...]", "Find matching track metadata."),
        new("enrich", "enrich <provider> <track-id>", "Preview enriched track metadata."),
        new("match-playlist", "match-playlist <playlist> [namespace ...]", "Enrich every track in a playlist."),
        new("lyrics", "lyrics <provider> <track-id>", "Find lyrics for a track."),
        new("sources", "sources <provider> <track-id>", "Find playable sources for a track."),
        new("remote-playlists", "remote-playlists <provider>", "List a provider's playlists."),
        new("import", "import <provider> <playlist-id>", "Import a remote playlist."),
        new("import-folder", "import-folder <path> [playlist-name]", "Import local audio from a folder."),
        new("playlists", "playlists", "List local playlists."),
        new("playlist", "playlist [playlist]", "Choose and inspect a local playlist."),
        new("show-playlist", "show-playlist <playlist>", "Inspect a local playlist."),
        new("create-playlist", "create-playlist <name>", "Create a local playlist."),
        new("add", "add <playlist> <provider> <track-id>", "Add a track to a playlist."),
        new("remove", "remove <playlist> <item-id>", "Remove an item from a playlist."),
        new("play", "play <provider> <track-id> [--source <provider>]", "Play one track."),
        new("play-playlist", "play-playlist <playlist> [start-number] [--source <provider>]", "Play a local playlist."),
        new("play-current", "play-current", "Play the selected queue item."),
        new("previous", "previous", "Play the previous queue item.", "prev"),
        new("next", "next", "Play the next queue item."),
        new("mode", "mode <sequential|repeat|shuffle>", "Set the playback order."),
        new("now-playing", "now-playing", "Show playback details."),
        new("pause", "pause", "Pause playback."),
        new("resume", "resume", "Resume playback."),
        new("stop", "stop", "Stop playback."),
        new("queue", "queue [clear]", "Show or clear the playback queue."),
        new("sync", "sync <status|pull|push>", "Synchronize the library."),
        new("save", "save", "Save the library and playback state."),
        new("exit", "exit", "Save and exit.", "quit")
    ];

    public static CommandDescriptor? Find(string name) =>
        Commands.FirstOrDefault(command => command.Matches(name));

    public static bool IsCommand(string name) => Find(name) is not null;
}
