using LemonNeo.Abstractions.Storage;

namespace LemonNeo.Desktop.Config;

public sealed record DesktopOptions(string PluginDirectory, string LibraryDirectory, bool EnableMockSync = false)
{
    public static DesktopOptions Parse(string[] args, IAppPathProvider paths)
    {
        var plugins = Path.Combine(AppContext.BaseDirectory, "plugins");
        var library = paths.MusicLibraryDirectory;
        var mockSync = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--plugins" when i + 1 < args.Length: plugins = Path.GetFullPath(args[++i]); break;
                case "--library" when i + 1 < args.Length: library = Path.GetFullPath(args[++i]); break;
                case "--mock-sync": mockSync = true; break;
                default: throw new ArgumentException($"Unknown or incomplete option '{args[i]}'.");
            }
        }
        return new(plugins, library, mockSync);
    }
}
