using System.Text;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Abstractions.Storage;
using LemonNeo.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LemonNeo.ConsoleApp;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (HighlightingTextWriter.CanUseColor)
            Console.SetOut(new HighlightingTextWriter(Console.Out));

        var paths = AppHost.CreateDefaultPaths();
        var options = ConsoleOptions.Parse(args, paths);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        try
        {
            await using var host = new AppHost(paths,
                new HostOptions(options.PluginDirectory, options.LibraryDirectory, paths.CacheDirectory), services =>
                {
                    services.AddSingleton(options);
                    services.AddSingleton(provider => new StartupCommands(
                        provider.GetRequiredService<IAppSettingsService>(),
                        paths, options.StartupFile));
                    services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information)
                        .AddSimpleConsole(console =>
                        {
                            console.SingleLine = true;
                            console.TimestampFormat = "HH:mm:ss ";
                        }));
                    services.AddSingleton<CommandCompletionService>();
                    services.AddSingleton<CommandHistory>();
                    services.AddSingleton<CommandProcessor>();
                });
            await host.InitializeAsync(cancellation.Token);
            var commands = host.Get<CommandProcessor>();
            if (await commands.RunStartupAsync(cancellation.Token))
                await commands.RunInteractiveAsync(cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Operation cancelled.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Fatal error: {exception.Message}");
            return 1;
        }
    }
}

internal sealed record ConsoleOptions(
    string PluginDirectory,
    string LibraryDirectory,
    string? StartupFile)
{
    public static ConsoleOptions Parse(string[] args, IAppPathProvider paths)
    {
        var plugins = Path.Combine(AppContext.BaseDirectory, "plugins");
        var library = paths.MusicLibraryDirectory;
        string? startup = null;
        for (var index = 0; index < args.Length; index++)
            switch (args[index])
            {
                case "--plugins" when index + 1 < args.Length: plugins = Path.GetFullPath(args[++index]); break;
                case "--library" when index + 1 < args.Length: library = Path.GetFullPath(args[++index]); break;
                case "--startup" when index + 1 < args.Length: startup = Path.GetFullPath(args[++index]); break;
                default: throw new ArgumentException($"Unknown or incomplete option '{args[index]}'.");
            }

        return new ConsoleOptions(plugins, library, startup);
    }
}