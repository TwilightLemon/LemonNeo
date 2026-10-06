using LemonNeo.Abstractions.Settings;
using LemonNeo.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LemonNeo.Hosting.Tests;

public sealed class SettingsPersistenceTests
{
    public sealed record FontSettings(int Size = 20);
    public sealed record LanguageSettings(string Language = "System");
    private static AppSettingsService Create(RecordingStore store, TimeProvider? time = null) =>
        new(store, NullLogger<AppSettingsService>.Instance, time);

    [Fact]
    public async Task PeriodicSaveCombinesChangesAndNotifiesOnlyTheChangedEntity()
    {
        var store = new RecordingStore();
        var time = new ManualTimeProvider();
        await using var service = Create(store, time);
        var desktop = service.ForPackage(PackageNames.Desktop);
        var plugin = service.ForPackage("LemonNeo.Plugin.Acceptance");
        await desktop.RegisterAsync("FontSettings", new FontSettings(), cancellationToken: TestContext.Current.CancellationToken);
        await desktop.RegisterAsync("LanguageSettings", new LanguageSettings(), cancellationToken: TestContext.Current.CancellationToken);
        await plugin.RegisterAsync("FontSettings", new FontSettings(), cancellationToken: TestContext.Current.CancellationToken);
        var fontNotifications = 0;
        var unrelatedNotifications = 0;
        using var fonts = desktop.Subscribe<FontSettings>(_ => fontNotifications++);
        using var languages = desktop.Subscribe<LanguageSettings>(_ => unrelatedNotifications++);
        using var otherPackage = plugin.Subscribe<FontSettings>(_ => unrelatedNotifications++);
        service.Start();

        for (var size = 21; size <= 120; size++) desktop.Apply<FontSettings>(current => current with { Size = size });
        desktop.Apply<FontSettings>(current => current with { Size = 120 });
        Assert.Equal(100, fontNotifications);
        Assert.Equal(0, unrelatedNotifications);
        Assert.Equal(20, plugin.Get<FontSettings>().Size);
        Assert.Empty(store.Writes);
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.Empty(store.Writes);
        time.Advance(TimeSpan.FromSeconds(1));
        await store.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await service.StopAsync();
        Assert.Single(store.Writes);
        Assert.Equal(120, store.Saved<FontSettings>(PackageNames.Desktop, "FontSettings")!.Size);
        Assert.Equal(100, fontNotifications);
        await service.SaveAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(store.Writes);
    }

    [Fact]
    public async Task ChangesDuringASlowSaveRemainDirtyWithoutBlockingApply()
    {
        var block = new WriteBlock();
        var store = new RecordingStore { BlockEntity = "FontSettings", Block = block };
        await using var service = Create(store);
        var scope = service.ForPackage(PackageNames.Desktop);
        await scope.RegisterAsync("FontSettings", new FontSettings(), cancellationToken: TestContext.Current.CancellationToken);
        scope.Apply<FontSettings>(_ => new(30));
        var save = service.SaveAsync(cancellationToken: TestContext.Current.CancellationToken);
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        scope.Apply<FontSettings>(_ => new(40));
        Assert.Equal(40, scope.Get<FontSettings>().Size);
        Assert.False(save.IsCompleted);
        block.Release.SetResult();
        await save;
        Assert.Equal(30, store.Saved<FontSettings>(PackageNames.Desktop, "FontSettings")!.Size);
        await service.SaveAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(40, store.Saved<FontSettings>(PackageNames.Desktop, "FontSettings")!.Size);
        Assert.Equal(2, store.WriteCount("FontSettings"));
    }

    [Fact]
    public async Task FailedEntityDoesNotPreventOtherSavesAndCanBeRetried()
    {
        var store = new RecordingStore { FailEntity = "FontSettings" };
        await using var service = Create(store);
        var scope = service.ForPackage(PackageNames.Desktop);
        await scope.RegisterAsync("FontSettings", new FontSettings(), cancellationToken: TestContext.Current.CancellationToken);
        await scope.RegisterAsync("LanguageSettings", new LanguageSettings(), cancellationToken: TestContext.Current.CancellationToken);
        scope.Apply<FontSettings>(_ => new(30));
        scope.Apply<LanguageSettings>(_ => new("English"));
        await Assert.ThrowsAsync<AggregateException>(() => service.SaveAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(30, scope.Get<FontSettings>().Size);
        Assert.Single(store.Writes);
        Assert.Equal("English", store.Saved<LanguageSettings>(PackageNames.Desktop, "LanguageSettings")!.Language);
        store.FailEntity = null;
        await service.SaveAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, store.WriteCount("FontSettings"));
        Assert.Equal(1, store.WriteCount("LanguageSettings"));
    }

    [Fact]
    public async Task CancelledSaveCanBeRetried()
    {
        var block = new WriteBlock();
        var store = new RecordingStore { BlockEntity = "FontSettings", Block = block };
        await using var service = Create(store);
        var scope = service.ForPackage(PackageNames.Desktop);
        await scope.RegisterAsync("FontSettings", new FontSettings(), cancellationToken: TestContext.Current.CancellationToken);
        scope.Apply<FontSettings>(_ => new(30));
        using var cancellation = new CancellationTokenSource();
        var save = service.SaveAsync(cancellation.Token);
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
        Assert.Empty(store.Writes);
        await service.SaveAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(30, store.Saved<FontSettings>(PackageNames.Desktop, "FontSettings")!.Size);
    }

    [Fact]
    public async Task ExitSavesLatestValueOnceEvenBeforeFirstTick()
    {
        var store = new RecordingStore();
        var service = Create(store, new ManualTimeProvider());
        var scope = service.ForPackage(PackageNames.Desktop);
        await scope.RegisterAsync("FontSettings", new FontSettings(), cancellationToken: TestContext.Current.CancellationToken);
        service.Start();
        scope.Apply<FontSettings>(_ => new(30));
        scope.Apply<FontSettings>(_ => new(45));
        await service.DisposeAsync();
        await service.DisposeAsync();
        Assert.Single(store.Writes);
        Assert.Equal(45, store.Saved<FontSettings>(PackageNames.Desktop, "FontSettings")!.Size);
        Assert.Throws<ObjectDisposedException>(() => scope.Get<FontSettings>());
    }

    [Fact]
    public async Task InvalidJsonIsReportedAndNeverOverwrittenWithDefaults()
    {
        using var paths = new TemporaryPaths();
        var file = paths.GetEntityConfigurationFile(PackageNames.Desktop, "FontSettings");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "{broken json", cancellationToken: TestContext.Current.CancellationToken);
        await using var service = new AppSettingsService(new JsonSettingsEntityStore(paths), NullLogger<AppSettingsService>.Instance);
        var scope = service.ForPackage(PackageNames.Desktop);
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => scope.RegisterAsync("FontSettings", new FontSettings(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => scope.Apply<FontSettings>(_ => new(30)));
        await service.SaveAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("{broken json", await File.ReadAllTextAsync(file, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegistrationLoadsOnceAndNormalizationAvoidsRedundantChanges()
    {
        var store = new RecordingStore();
        await using var service = Create(store);
        var scope = service.ForPackage(PackageNames.Desktop);
        scope.Register("FontSettings", new FontSettings(), value => value with { Size = Math.Clamp(value.Size, 16, 48) });
        Assert.Equal(20, scope.Get<FontSettings>().Size);
        Assert.Throws<InvalidOperationException>(() => scope.Apply<FontSettings>(_ => new(30)));
        await service.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        await scope.RegisterAsync("FontSettings", new FontSettings(), cancellationToken: TestContext.Current.CancellationToken);
        await service.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(store.Reads);
        var notifications = 0;
        using var subscription = scope.Subscribe<FontSettings>(_ => notifications++);
        scope.Apply<FontSettings>(_ => new(100));
        scope.Apply<FontSettings>(_ => new(200));
        Assert.Equal(48, scope.Get<FontSettings>().Size);
        Assert.Equal(1, notifications);
        await service.SaveAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(store.Writes);
    }

    [Fact]
    public async Task PackageReleaseSavesItsEntitiesAndAllowsRegistrationWithANewType()
    {
        var store = new RecordingStore();
        await using var service = Create(store);
        const string package = "LemonNeo.Plugin.Acceptance";
        var plugin = service.ForPackage(package);
        var desktop = service.ForPackage(PackageNames.Desktop);
        await plugin.RegisterAsync("AccountSettings", new FontSettings(), cancellationToken: TestContext.Current.CancellationToken);
        await desktop.RegisterAsync("FontSettings", new FontSettings(), cancellationToken: TestContext.Current.CancellationToken);
        plugin.Apply<FontSettings>(_ => new(30));
        desktop.Apply<FontSettings>(_ => new(40));
        await service.ReleasePackageAsync(package, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(store.Writes);
        Assert.Throws<ObjectDisposedException>(() => plugin.Get<FontSettings>());
        var reloaded = service.ForPackage(package);
        await reloaded.RegisterAsync("AccountSettings", new ReplacementSettings(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(30, reloaded.Get<ReplacementSettings>().Size);
        await service.SaveAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, store.WriteCount("AccountSettings"));
        Assert.Equal(1, store.WriteCount("FontSettings"));
    }

    public sealed record ReplacementSettings(int Size = 1);

    [Fact]
    public async Task PeriodicSaveRetriesFailedEntitiesOnTheNextTick()
    {
        var time = new ManualTimeProvider();
        var store = new RecordingStore { FailEntity = "FontSettings" };
        await using var service = Create(store, time);
        var scope = service.ForPackage(PackageNames.Desktop);
        await scope.RegisterAsync("FontSettings", new FontSettings(), cancellationToken: TestContext.Current.CancellationToken);
        scope.Apply<FontSettings>(_ => new(30));
        service.Start();
        time.Advance(TimeSpan.FromSeconds(10));
        await store.FailedWrite.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Empty(store.Writes);
        store.FailEntity = null;
        time.Advance(TimeSpan.FromSeconds(10));
        await store.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await service.StopAsync();
        Assert.Single(store.Writes);
        Assert.Equal(30, store.Saved<FontSettings>(PackageNames.Desktop, "FontSettings")!.Size);
    }

    [Fact]
    public async Task PackageReleaseStopsNewUpdatesWhileItsFinalSnapshotIsBeingSaved()
    {
        var block = new WriteBlock();
        var store = new RecordingStore { BlockEntity = "FontSettings", Block = block };
        await using var service = Create(store);
        var scope = service.ForPackage("LemonNeo.Plugin.Acceptance");
        await scope.RegisterAsync("FontSettings", new FontSettings(), cancellationToken: TestContext.Current.CancellationToken);
        scope.Apply<FontSettings>(_ => new(30));
        var release = service.ReleasePackageAsync(scope.PackageId, TestContext.Current.CancellationToken);
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Throws<ObjectDisposedException>(() => scope.Apply<FontSettings>(_ => new(40)));
        block.Release.SetResult();
        await release;
        Assert.Equal(30, store.Saved<FontSettings>(scope.PackageId, "FontSettings")!.Size);
    }

    [Fact]
    public async Task FailedReleaseRetainsThePackageForRetry()
    {
        var store = new RecordingStore { FailEntity = "FontSettings" };
        await using var service = Create(store);
        var scope = service.ForPackage("LemonNeo.Plugin.Acceptance");
        await scope.RegisterAsync("FontSettings", new FontSettings(), cancellationToken: TestContext.Current.CancellationToken);
        scope.Apply<FontSettings>(_ => new(30));
        await Assert.ThrowsAsync<AggregateException>(() => service.ReleasePackageAsync(scope.PackageId, cancellationToken: TestContext.Current.CancellationToken));
        scope.Apply<FontSettings>(_ => new(40));
        store.FailEntity = null;
        await service.ReleasePackageAsync(scope.PackageId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(40, store.Saved<FontSettings>(scope.PackageId, "FontSettings")!.Size);
    }

    [Theory]
    [InlineData("lemonneo.desktop")]
    [InlineData("LemonNeo.Plugin../Escape")]
    [InlineData("LemonNeo.Plugin-QqMusic")]
    [InlineData("LemonNeo..Desktop")]
    public async Task PackageNamesRejectNonCSharpNamesAndPaths(string name)
    {
        await using var service = Create(new RecordingStore());
        Assert.Throws<ArgumentException>(() => service.ForPackage(name));
    }

    [Fact]
    public async Task EntityRegistrationRejectsAmbiguousFilesAndTypes()
    {
        await using var service = Create(new RecordingStore());
        var scope = service.ForPackage(PackageNames.Desktop);
        await scope.RegisterAsync("FontSettings", new FontSettings(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => scope.Register("DifferentFile", new FontSettings()));
        Assert.Throws<InvalidOperationException>(() => scope.Register("FontSettings", new LanguageSettings()));
        Assert.Throws<ArgumentException>(() => scope.Register("../Invalid", new LanguageSettings()));
    }
}
