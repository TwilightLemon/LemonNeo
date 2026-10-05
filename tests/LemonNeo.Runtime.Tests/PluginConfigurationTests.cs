using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json.Serialization.Metadata;
using LemonNeo.Plugin.Abstractions;
using LemonNeo.Plugin.Runtime;
using Xunit;

namespace LemonNeo.Runtime.Tests;

public sealed class PluginConfigurationTests
{
    private const string Package = "LemonNeo.Plugin.ConfigurationFixture";
    public sealed record AccountSettings(string State = "Default");
    private static AppRuntime Create(TemporaryPaths paths, string pluginDirectory) =>
        new(paths, new(pluginDirectory, paths.MusicLibraryDirectory));

    [Fact]
    public async Task PluginShutdownChangesAreSavedBeforeItsConfigurationIsReleased()
    {
        using var paths = new TemporaryPaths();
        var file = paths.GetEntityConfigurationFile(Package, "AccountSettings");
        PluginLoader loader;
        await using (var runtime = Create(paths, Path.Combine(AppContext.BaseDirectory, "fixtures")))
        {
            await runtime.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
            await runtime.Settings.StopAsync();
            loader = runtime.Get<PluginLoader>();
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
        await using var runtime = Create(paths, Path.Combine(AppContext.BaseDirectory, "fixtures"));
        await runtime.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await runtime.Settings.StopAsync();
        var loader = runtime.Get<PluginLoader>();
        var scope = runtime.Settings.ForPackage(Package);
        var plugin = Assert.Single(loader.Loaded);
        await plugin.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => scope.Register("HostSettings", new AccountSettings()));
        var reloaded = runtime.Settings.ForPackage(Package);
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
        await using var runtime = Create(paths, paths.PluginDirectory);
        await runtime.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await runtime.Settings.StopAsync();
        var loader = runtime.Get<PluginLoader>();
        Assert.Single(loader.Loaded);
        Assert.Single(loader.LastReport.Failures);
        Assert.Contains("already loaded", loader.LastReport.Failures[0].Error);
        await runtime.Settings.SaveAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(paths.GetEntityConfigurationFile(Package, "AccountSettings"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("Initialized", json.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ConfigurationAndLoadReportsDoNotKeepThePluginAssemblyAliveAfterShutdown()
    {
        using var paths = new TemporaryPaths();
        var context = await LoadAndUnloadAsync(paths);
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
    private static async Task<WeakReference> LoadAndUnloadAsync(TemporaryPaths paths)
    {
        await using var runtime = Create(paths, Path.Combine(AppContext.BaseDirectory, "fixtures"));
        await runtime.InitializeAsync(TestContext.Current.CancellationToken);
        await runtime.Settings.StopAsync();
        var plugin = Assert.Single(runtime.Get<PluginLoader>().Loaded);
        return new WeakReference(AssemblyLoadContext.GetLoadContext(plugin.Instance.GetType().Assembly));
    }

}
