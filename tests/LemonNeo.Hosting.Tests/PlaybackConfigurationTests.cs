using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Application.Playback;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using LemonNeo.Infrastructure.Settings;
using System.Text.Json;
using Xunit;

namespace LemonNeo.Hosting.Tests;

public sealed class PlaybackConfigurationTests
{
    [Fact]
    public async Task VolumeAndSeekChangeMemoryAndSaveSmallEntitiesWithoutRewritingQueue()
    {
        using var paths = new TemporaryPaths();
        var store = new RecordingStore(new JsonSettingsEntityStore(paths));
        await using var host = HostFixture.Create(paths, new TestPlayer(), store);
        await host.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await host.Settings.StopAsync();
        var session = host.Get<PlaybackSessionService>();
        await session.PlayPlaylistAsync([HostFixture.Track(), HostFixture.Track("two")], cancellationToken: TestContext.Current.CancellationToken);
        await host.Settings.SaveAsync(cancellationToken: TestContext.Current.CancellationToken);
        var count = store.Writes.Count;
        await session.SetVolumeAsync(.3, cancellationToken: TestContext.Current.CancellationToken);
        await session.SeekAsync(TimeSpan.FromSeconds(40), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(count, store.Writes.Count);
        var scope = host.Settings.ForPackage(PackageNames.Application);
        Assert.Equal(.3, scope.Get<PlaybackPreferences>().Volume);
        Assert.Equal(TimeSpan.FromSeconds(40), scope.Get<PlaybackResumeState>().Position);
        await host.Settings.SaveAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, store.WriteCount(nameof(PlaybackQueueState)));
        Assert.Equal(2, store.WriteCount(nameof(PlaybackResumeState)));
        Assert.Equal(1, store.WriteCount(nameof(PlaybackPreferences)));
        using var queueJson = JsonDocument.Parse(await File.ReadAllTextAsync(paths.GetEntityConfigurationFile(PackageNames.Application, nameof(PlaybackQueueState)), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, queueJson.RootElement.GetProperty("items").GetArrayLength());
        Assert.False(queueJson.RootElement.TryGetProperty("position", out _));
        Assert.False(queueJson.RootElement.TryGetProperty("volume", out _));
        Assert.False(File.Exists(Path.Combine(paths.MusicLibraryDirectory, "playback.json")));
    }

    [Fact]
    public async Task SlowDiskDoesNotBlockVolumeOrSeekAndLatestValuesAreSaved()
    {
        using var paths = new TemporaryPaths();
        var store = new RecordingStore();
        await using var host = HostFixture.Create(paths, new TestPlayer(), store);
        await host.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await host.Settings.StopAsync();
        var session = host.Get<PlaybackSessionService>();
        await session.PlayTrackAsync(HostFixture.Track(), cancellationToken: TestContext.Current.CancellationToken);
        await host.Settings.SaveAsync(cancellationToken: TestContext.Current.CancellationToken);
        await session.SetVolumeAsync(.4, cancellationToken: TestContext.Current.CancellationToken);
        var block = new WriteBlock();
        store.BlockEntity = nameof(PlaybackPreferences);
        store.Block = block;
        var save = host.Settings.SaveAsync(cancellationToken: TestContext.Current.CancellationToken);
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await session.SetVolumeAsync(.6, cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await session.SeekAsync(TimeSpan.FromSeconds(55), cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(save.IsCompleted);
        block.Release.SetResult();
        await save;
        await host.Settings.SaveAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(.6, store.Saved<PlaybackPreferences>(PackageNames.Application, nameof(PlaybackPreferences))!.Volume);
        Assert.Equal(TimeSpan.FromSeconds(55), store.Saved<PlaybackResumeState>(PackageNames.Application, nameof(PlaybackResumeState))!.Position);
        Assert.Equal(1, store.WriteCount(nameof(PlaybackQueueState)));
    }

    [Fact]
    public async Task ShutdownCapturesPositionBeforeStoppingAndRestoresWithoutAutoPlaying()
    {
        using var paths = new TemporaryPaths();
        var firstPlayer = new TestPlayer();
        Guid currentItem;
        Guid[] shuffle;
        await using (var host = HostFixture.Create(paths, firstPlayer))
        {
            await host.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
            await host.Settings.StopAsync();
            var session = host.Get<PlaybackSessionService>();
            await session.PlayPlaylistAsync([HostFixture.Track(), HostFixture.Track("two"), HostFixture.Track("three")], 1, cancellationToken: TestContext.Current.CancellationToken);
            await session.SetModeAsync(PlaybackMode.Shuffle, cancellationToken: TestContext.Current.CancellationToken);
            await session.SetVolumeAsync(.25, cancellationToken: TestContext.Current.CancellationToken);
            await session.SeekAsync(TimeSpan.FromSeconds(45), cancellationToken: TestContext.Current.CancellationToken);
            currentItem = session.Queue.CurrentItem!.Id;
            shuffle = session.Queue.Snapshot().ShuffleOrder.ToArray();
            Assert.Equal(PlayerState.Playing, firstPlayer.State);
        }
        Assert.Equal(TimeSpan.Zero, firstPlayer.Timeline.Position);
        Assert.Equal(PlayerState.Stopped, firstPlayer.State);

        var secondPlayer = new TestPlayer();
        await using var restored = HostFixture.Create(paths, secondPlayer);
        await restored.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await restored.Settings.StopAsync();
        var session2 = restored.Get<PlaybackSessionService>();
        Assert.Equal(3, session2.Queue.Items.Count);
        Assert.Equal(currentItem, session2.Queue.CurrentItem!.Id);
        Assert.Equal(shuffle, session2.Queue.Snapshot().ShuffleOrder.ToArray());
        Assert.Equal(PlaybackMode.Shuffle, session2.Queue.Mode);
        Assert.Equal(TimeSpan.FromSeconds(45), session2.Snapshot.Timeline.Position);
        Assert.Equal(PlaybackStatus.Playing, session2.Snapshot.RestoredStatus);
        Assert.Equal(PlayerState.Stopped, session2.Snapshot.State);
        Assert.Equal(0, secondPlayer.PlayCalls);
        Assert.Equal(.25, session2.Volume);
        await session2.PlayCurrentAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromSeconds(45), secondPlayer.Timeline.Position);
        Assert.Equal(.25, secondPlayer.Volume);
        Assert.Equal(1, secondPlayer.PlayCalls);
    }

    [Fact]
    public async Task ResumeForAnotherQueueItemDoesNotSeekTheCurrentSong()
    {
        using var paths = new TemporaryPaths();
        await using (var host = HostFixture.Create(paths, new TestPlayer()))
        {
            await host.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
            await host.Settings.StopAsync();
            await host.Get<PlaybackSessionService>().PlayTrackAsync(HostFixture.Track(), cancellationToken: TestContext.Current.CancellationToken);
        }
        var store = new JsonSettingsEntityStore(paths);
        await store.SaveAsync(PackageNames.Application, nameof(PlaybackResumeState), new PlaybackResumeState(Guid.NewGuid(), TimeSpan.FromSeconds(99), PlaybackStatus.Playing), cancellationToken: TestContext.Current.CancellationToken);
        var player = new TestPlayer();
        await using var restored = HostFixture.Create(paths, player);
        await restored.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await restored.Settings.StopAsync();
        var session = restored.Get<PlaybackSessionService>();
        Assert.Equal(TimeSpan.Zero, session.Snapshot.Timeline.Position);
        await session.PlayCurrentAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.Zero, player.Timeline.Position);
    }

    [Fact]
    public async Task ConfigurationWriteFailureDoesNotStopPlayback()
    {
        using var paths = new TemporaryPaths();
        var store = new RecordingStore { FailEntity = nameof(PlaybackQueueState) };
        var player = new TestPlayer();
        await using var host = HostFixture.Create(paths, player, store);
        await host.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await host.Settings.StopAsync();
        var session = host.Get<PlaybackSessionService>();
        await session.PlayTrackAsync(HostFixture.Track(), cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<AggregateException>(() => host.Settings.SaveAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(PlayerState.Playing, player.State);
        await session.SeekAsync(TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromSeconds(30), player.Timeline.Position);
        store.FailEntity = null;
        await host.Settings.SaveAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, store.WriteCount(nameof(PlaybackQueueState)));
    }
}
