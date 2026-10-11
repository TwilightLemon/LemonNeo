namespace LemonNeo.ConsoleApp;

internal sealed record CommandDescriptor(
    string Name,
    string Usage,
    string Description,
    params string[] Aliases)
{
    public bool Matches(string value)
    {
        return string.Equals(Name, value, StringComparison.OrdinalIgnoreCase) ||
               Aliases.Contains(value, StringComparer.OrdinalIgnoreCase);
    }
}

internal static class CommandCatalog
{
    public static IReadOnlyList<CommandDescriptor> Commands { get; } =
    [
        new("help", "help", "Show commands and keyboard shortcuts."),
        new("version", "version", "Show the application version."),
        new("plugins", "plugins", "Show complete plugin information and load failures."),
        new("library", "library", "Show playlist, unique track, and item counts."),
        new("startup", "startup [show|set <file>|reset]", "Inspect or configure startup commands."),
        new("providers", "providers", "List music providers and capabilities."),
        new("players", "players", "List playback engines."),
        new("paths", "paths", "Show configuration and library paths."),
        new("auth", "auth <auth-provider>", "Show provider authentication state."),
        new("login", "login <auth-provider> [cookie]", "Sign in to a provider."),
        new("logout", "logout <auth-provider>", "Sign out from a provider."),
        new("search", "search <keyword> [--source <namespace>]", "Search tracks across providers."),
        new("metadata", "metadata <track-provider> <track-id> [namespace ...]", "Find matching track metadata."),
        new("enrich", "enrich <track-provider> <track-id>", "Preview enriched track metadata."),
        new("match-playlist", "match-playlist <playlist> [namespace ...]", "Enrich every track in a playlist."),
        new("lyrics", "lyrics <track-provider> <track-id>", "Find lyrics for a track."),
        new("sources", "sources <track-provider> <track-id>", "Find playable sources for a track."),
        new("remote-playlists", "remote-playlists <playlist-provider>", "List a provider's playlists."),
        new("import", "import <playlist-provider> <playlist-id>", "Import a remote playlist."),
        new("import-folder", "import-folder <path> [playlist-name]", "Import local audio from a folder."),
        new("playlists", "playlists", "List local playlists."),
        new("playlist", "playlist [playlist]", "Choose and inspect a local playlist."),
        new("show-playlist", "show-playlist <playlist>", "Inspect a local playlist."),
        new("create-playlist", "create-playlist <name> [--description <text>]", "Create a local playlist."),
        new("update-playlist", "update-playlist <playlist> [--name <name>] [--description <text>]",
            "Edit playlist fields; use \"\" to clear description."),
        new("delete-playlist", "delete-playlist <playlist>", "Delete a local playlist and its items."),
        new("add", "add <playlist> <track-provider> <track-id>", "Add a track to a playlist."),
        new("remove", "remove <playlist> <item>", "Remove a track by number, item ID, or ID prefix."),
        new("show-track", "show-track <playlist> <item>", "Show a playlist track and its external IDs."),
        new("create-track", "create-track <playlist> <title> [--<field> <value> ...]",
            "Create a track snapshot in a playlist."),
        new("update-track", "update-track <playlist> <item> --<field> <value> [...]",
            "Edit title, artists (; separated), album, duration (seconds), artwork, provider, track-id."),
        new("move-track", "move-track <playlist> <item> <position>", "Move a track to a one-based playlist position."),
        new("external-ids", "external-ids <playlist> <item> [add|update|remove <namespace> <kind|-> [value]]",
            "List or edit qualified external IDs; - means no kind."),
        new("play", "play <track-provider> <track-id> [--source <source-provider>]", "Play one track."),
        new("play-playlist", "play-playlist <playlist> [start-number] [--source <source-provider>]",
            "Play a local playlist."),
        new("play-current", "play-current", "Play the selected queue item."),
        new("previous", "previous", "Play the previous queue item.", "prev"),
        new("next", "next", "Play the next queue item."),
        new("mode", "mode <sequential|repeat|shuffle>", "Set the playback order."),
        new("now-playing", "now-playing", "Show playback details."),
        new("pause", "pause", "Pause playback."),
        new("resume", "resume", "Resume playback."),
        new("stop", "stop", "Stop playback."),
        new("queue", "queue [clear]", "Show or clear the playback queue."),
        new("save", "save", "Save the library and playback state."),
        new("exit", "exit", "Save and exit.", "quit")
    ];

    public static CommandDescriptor? Find(string name)
    {
        return Commands.FirstOrDefault(command => command.Matches(name));
    }

    public static bool IsCommand(string name)
    {
        return Find(name) is not null;
    }
}