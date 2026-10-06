using System.Reflection;
using LemonNeo.Application.Sync;
using LemonNeo.Infrastructure.Plugins;
using Xunit;

namespace LemonNeo.Hosting.Tests;

public sealed class ArchitectureTests
{
    [Theory]
    [InlineData("Core", "")]
    [InlineData("Abstractions", "Core")]
    [InlineData("Application", "Core,Abstractions")]
    [InlineData("Infrastructure", "Core,Abstractions")]
    [InlineData("Hosting", "Core,Abstractions,Application,Infrastructure")]
    [InlineData("Desktop", "Core,Abstractions,Application,Hosting")]
    [InlineData("Console", "Core,Abstractions,Application,Hosting")]
    public void CompiledAssembliesRespectModuleBoundaries(string module, string dependencies)
    {
        var allowed = dependencies.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(name => "LemonNeo." + name).ToHashSet(StringComparer.Ordinal);
        var assembly = Assembly.Load("LemonNeo." + module);
        foreach (var dependency in assembly.GetReferencedAssemblies()
                     .Where(reference => reference.Name!.StartsWith("LemonNeo.", StringComparison.Ordinal)))
            Assert.Contains(dependency.Name!, allowed);
    }

    [Fact]
    public void InfrastructureImplementationsAreNotPublicApi()
    {
        Assert.Empty(typeof(PluginLoader).Assembly.GetExportedTypes());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MockSyncRequiresExplicitSelection(bool enableMockSync)
    {
        using var paths = new TemporaryPaths();
        await using var host = new AppHost(paths,
            new(paths.PluginDirectory, paths.MusicLibraryDirectory, enableMockSync));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        var sync = host.Get<SyncService>();
        if (enableMockSync)
        {
            Assert.Equal("mock-git", Assert.Single(sync.ProviderIds));
            var result = await sync.FetchRemoteAsync("mock-git", TestContext.Current.CancellationToken);
            Assert.True(result.Success);
            Assert.Single(result.RemoteLibrary!.Playlists);
        }
        else
            Assert.Empty(sync.ProviderIds);
    }
}
