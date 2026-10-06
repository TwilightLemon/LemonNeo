using LemonNeo.Abstractions.Settings;
using LemonNeo.ConsoleApp;
using Xunit;

namespace LemonNeo.Hosting.Tests;

public sealed class ConsoleConfigurationTests
{
    [Fact]
    public async Task StartupScriptUsesConsoleEntityAndFlushesOnExit()
    {
        using var paths = new TemporaryPaths();
        var file = paths.GetEntityConfigurationFile(PackageNames.Console, nameof(StartupPreferences));
        var applicationFile = paths.GetEntityConfigurationFile(PackageNames.Application, nameof(StartupPreferences));
        await using (var host = HostFixture.Create(paths, new TestPlayer()))
        {
            await host.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
            await host.Settings.StopAsync();
            var startup = new StartupCommands(host.Settings, paths);
            Assert.Equal(file, startup.FilePath);
            await startup.WriteAsync("version\r\nlibrary\r\n", cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(File.Exists(file));
            Assert.Equal(["version", "library", ""], await startup.LoadAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
        Assert.True(File.Exists(file));
        Assert.False(File.Exists(applicationFile));
        await using var restored = HostFixture.Create(paths, new TestPlayer());
        await restored.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await restored.Settings.StopAsync();
        var restoredStartup = new StartupCommands(restored.Settings, paths);
        Assert.Equal(["version", "library", ""], await restoredStartup.LoadAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DefaultStartupScriptDoesNotCreateConfigurationFiles()
    {
        using var paths = new TemporaryPaths();
        var file = paths.GetEntityConfigurationFile(PackageNames.Console, nameof(StartupPreferences));
        await using (var host = HostFixture.Create(paths, new TestPlayer()))
        {
            await host.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
            await host.Settings.StopAsync();
            var startup = new StartupCommands(host.Settings, paths);
            Assert.Equal(StartupCommands.DefaultScript.Split('\n'), await startup.LoadAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task ExplicitStartupScriptReadsAndWritesSelectedFile()
    {
        using var paths = new TemporaryPaths();
        var scriptFile = Path.Combine(paths.Root, "startup.script");
        var configurationFile = paths.GetEntityConfigurationFile(PackageNames.Console, nameof(StartupPreferences));
        await File.WriteAllTextAsync(scriptFile, "version\r\nlibrary\r\n", cancellationToken: TestContext.Current.CancellationToken);
        await using (var host = HostFixture.Create(paths, new TestPlayer()))
        {
            await host.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
            await host.Settings.StopAsync();
            var startup = new StartupCommands(host.Settings, paths, scriptFile);
            Assert.Equal(scriptFile, startup.FilePath);
            Assert.Equal(["version", "library"], await startup.LoadAsync(cancellationToken: TestContext.Current.CancellationToken));
            await startup.WriteAsync("plugins\n", cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("plugins\n", await File.ReadAllTextAsync(scriptFile, cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(File.Exists(scriptFile + ".tmp"));
        }
        Assert.False(File.Exists(configurationFile));
    }

    [Fact]
    public async Task SharedRuntimeDoesNotRegisterOrLoadStartupScripts()
    {
        using var paths = new TemporaryPaths();
        foreach (var package in new[] { PackageNames.Application, PackageNames.Console })
        {
            var file = paths.GetEntityConfigurationFile(package, nameof(StartupPreferences));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "invalid JSON", cancellationToken: TestContext.Current.CancellationToken);
        }
        await using var host = HostFixture.Create(paths, new TestPlayer());
        await host.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => host.Get<StartupCommands>());
    }
}
