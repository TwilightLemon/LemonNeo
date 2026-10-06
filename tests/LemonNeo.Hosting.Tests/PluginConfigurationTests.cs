using LemonNeo.Infrastructure.Plugins;
using LemonNeo.Abstractions.Plugins;
using LemonNeo.Hosting;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json.Serialization.Metadata;
using Xunit;

namespace LemonNeo.Hosting.Tests;

public sealed class PluginConfigurationTests
{
    private const string Package = "LemonNeo.Plugin.ConfigurationFixture";
    public sealed record AccountSettings(string State = "Default");
    private static AppHost Create(TemporaryPaths paths, string pluginDirectory) =>
        new(paths, new(pluginDirectory, paths.MusicLibraryDirectory));

    [Fact]
    public async Task PluginShutdownChangesAreSavedBeforeItsConfigurationIsReleased()
    {
        using var paths = new TemporaryPaths();
        var file = paths.GetEntityConfigurationFile(Package, "AccountSettings");
        PluginLoader loader;
        await using (var host = Create(paths, Path.Combine(AppContext.BaseDirectory, "fixtures")))
        {
            await host.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
            await host.Settings.StopAsync();
            loader = host.Get<PluginLoader>();
            Assert.Empty(loader.LastReport.Failures);
            Assert.Single(loader.Loaded);
            Assert.False(File.Exists(file));
        }
        Assert.Empty(loader.Loaded);
        Assert.Empty(loader.LastReport.Loaded);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("Shutdown", json.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ReleasedPluginScopeCanBeRegisteredUsingAHostType()
    {
        using var paths = new TemporaryPaths();
        await using var host = Create(paths, Path.Combine(AppContext.BaseDirectory, "fixtures"));
        await host.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await host.Settings.StopAsync();
        var loader = host.Get<PluginLoader>();
        var scope = host.Settings.ForPackage(Package);
        var plugin = Assert.Single(loader.Loaded);
        await plugin.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => scope.Register("HostSettings", new AccountSettings()));
        var reloaded = host.Settings.ForPackage(Package);
        await reloaded.RegisterAsync("AccountSettings", new AccountSettings(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Shutdown", reloaded.Get<AccountSettings>().State);
    }

    [Fact]
    public async Task DuplicatePluginPackageIsRejectedWithoutReleasingTheLoadedPluginsScope()
    {
        using var paths = new TemporaryPaths();
        foreach (var name in new[] { "First", "Second" })
        {
            var target = Path.Combine(paths.PluginDirectory, name);
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fixtures")))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        var context = await CheckDuplicateAndUnloadAsync(paths);
        await AssertUnloadedAsync(context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> CheckDuplicateAndUnloadAsync(TemporaryPaths paths)
    {
        await using var host = Create(paths, paths.PluginDirectory);
        await host.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await host.Settings.StopAsync();
        var loader = host.Get<PluginLoader>();
        Assert.Single(loader.Loaded);
        Assert.Single(loader.LastReport.Failures);
        Assert.Contains("already loaded", loader.LastReport.Failures[0].Error);
        await host.Settings.SaveAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(paths.GetEntityConfigurationFile(Package, "AccountSettings"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("Initialized", json.RootElement.GetProperty("state").GetString());
        var plugin = Assert.Single(loader.Loaded);
        return new WeakReference(AssemblyLoadContext.GetLoadContext(plugin.Instance.GetType().Assembly));
    }

    [Fact]
    public async Task ConfigurationAndLoadReportsDoNotKeepThePluginAssemblyAliveAfterShutdown()
    {
        using var paths = new TemporaryPaths();
        var (context, report) = await LoadAndUnloadAsync(paths);
        await AssertUnloadedAsync(context);
        GC.KeepAlive(report);
    }

    private static async Task AssertUnloadedAsync(WeakReference context)
    {
        // Let the loader/disposal continuation leave its stack before checking collectible references.
        await Task.Yield();
        for (var attempt = 0; context.IsAlive && attempt < 30; attempt++)
        {
            // System.Text.Json's reflection accessor cache expires inactive entries after one second
            // on subsequent metadata creation. Exercise a host entity without retaining plugin types.
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
            _ = JsonSerializer.Serialize(new AccountSettings(), new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() });
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(context.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Context, PluginLoadReport Report)> LoadAndUnloadAsync(TemporaryPaths paths)
    {
        await using var host = Create(paths, Path.Combine(AppContext.BaseDirectory, "fixtures"));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        await host.Settings.StopAsync();
        var plugin = Assert.Single(host.Get<PluginLoader>().Loaded);
        // Retaining a diagnostics snapshot must not retain the collectible plugin assembly.
        return (new WeakReference(AssemblyLoadContext.GetLoadContext(plugin.Instance.GetType().Assembly)),
            host.Plugins.LastReport);
    }

}
