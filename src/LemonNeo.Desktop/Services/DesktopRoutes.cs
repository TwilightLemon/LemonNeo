namespace LemonNeo.Desktop.Services;

public static class DesktopRoutes
{
    public const string Library = "Library",
        Search = "Search",
        Accounts = "Accounts",
        Online = "Online",
        Queue = "Queue",
        Lyrics = "Lyrics",
        Editor = "Editor",
        MatchPlaylist = "MatchPlaylist",
        Tools = "Tools",
        Settings = "Settings";

    public static IReadOnlyList<string> All { get; } =
        [Library, Search, Accounts, Online, Queue, Lyrics, Editor, MatchPlaylist, Tools, Settings];
}