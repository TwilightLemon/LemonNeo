using Avalonia;

namespace LemonNeo.Desktop;

internal static class Program
{
    internal static string[] Arguments { get; private set; } = [];

    [STAThread]
    public static void Main(string[] args)
    {
        Arguments = args;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
