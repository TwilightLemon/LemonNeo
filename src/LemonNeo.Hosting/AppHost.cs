using LemonNeo.Abstractions.Plugins;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Abstractions.Storage;
using LemonNeo.Application.Library;
using LemonNeo.Application.Playback;
using LemonNeo.Infrastructure.Paths;
using LemonNeo.Infrastructure.Plugins;
using LemonNeo.Hosting.Registration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Hosting;

/// <summary>The shared Desktop/Console owner of configuration, plugins, playback and shutdown.</summary>
public class AppHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly HostOptions _options;
    private bool _pluginsLoaded;
    private bool _libraryLoaded;
    private Task? _disposeTask;

    public AppHost(IAppPathProvider paths, HostOptions options, Action<IServiceCollection>? configure = null)
    {
        Paths = paths;
        _options = options;
        var services = new ServiceCollection();
        services.AddInfrastructure(paths, options);
        services.AddApplication(paths);
        configure?.Invoke(services);
        _services = services.BuildServiceProvider();
    }

    public static IAppPathProvider CreateDefaultPaths() => AppPathProvider.CreateDefault();

    public IPluginDiagnostics Plugins => Get<IPluginDiagnostics>();
    public IAppPathProvider Paths { get; }
    public IAppSettingsService Settings => Get<IAppSettingsService>();
    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Settings.LoadAsync(cancellationToken);
        await Get<PlaybackSessionService>().InitializeAsync(cancellationToken);
        if (!_pluginsLoaded)
        {
            await Get<PluginLoader>().LoadFromDirectoryAsync(_options.PluginDirectory, cancellationToken);
            _pluginsLoaded = true;
        }
        await Get<LibraryService>().LoadAsync(cancellationToken);
        _libraryLoaded = true;
        Settings.Start();
    }

    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        List<Exception> errors = [];
        async Task Finish(Func<Task> action)
        {
            try { await action(); }
            catch (Exception exception) { errors.Add(exception); }
        }
        await Finish(Settings.StopAsync);
        await Finish(Get<PlaybackSessionService>().PrepareForShutdownAsync);
        await Finish(Get<PluginLoader>().ShutdownAsync);
        if (_libraryLoaded) await Finish(() => Get<LibraryService>().SaveAsync());
        await Finish(() => Settings.SaveAsync());
        await Finish(() => _services.DisposeAsync().AsTask());
        if (errors.Count > 0) throw new AggregateException("Host shutdown failed.", errors);
    }
}
