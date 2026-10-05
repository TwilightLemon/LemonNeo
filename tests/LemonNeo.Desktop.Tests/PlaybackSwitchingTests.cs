using LemonNeo.Application;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Core;
using LemonNeo.Playback;
using LemonNeo.Plugin.Abstractions;
using LemonNeo.Plugin.Runtime;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.ViewModels;
using LemonNeo.Desktop.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class PlaybackSwitchingTests
{
    private static readonly Track First = new() { Id = new("source", "first"), Title = "First" };
    private static readonly Track Second = new() { Id = new("source", "second"), Title = "Second" };
    private static readonly HttpPlaybackSource Source = new() { Uri = new("https://audio.test/song.mp3") };

    [Theory]
    [InlineData("track")]
    [InlineData("playlist")]
    [InlineData("current")]
    [InlineData("queue-item")]
    [InlineData("next")]
    [InlineData("previous")]
    public async Task EverySessionPlaybackEntryStopsBeforeSourceResolution(string command)
    {
        using var paths = new TestPaths();
        var player = new Player();
        var provider = new SourceProvider();
        var resolver = Resolver(paths, provider, player);
        await using var session = new PlaybackSessionService(resolver, new PlaybackQueue(), new Repository(),
            NullLogger<PlaybackSessionService>.Instance);
        var cancellationToken = TestContext.Current.CancellationToken;
        await session.InitializeAsync(cancellationToken);
        await session.PlayPlaylistAsync([First, Second], command == "previous" ? 1 : 0,
            cancellationToken: cancellationToken);
        var expected = command == "previous" || command == "current" ? First : Second;

        provider.Resolve = (_, _) =>
        {
            Assert.Equal(PlayerState.Stopped, player.State);
            Assert.Null(resolver.ActivePlayer);
            Assert.Null(resolver.CurrentQueueTrack);
            Assert.Equal(expected, session.Snapshot.CurrentTrack);
            Assert.Equal(PlayerState.Stopped, session.Snapshot.State);
            Assert.Equal(TimeSpan.Zero, session.GetCurrentSnapshot().Timeline.Position);
            return Task.FromResult<PlaybackSource?>(Source);
        };

        switch (command)
        {
            case "track": await session.PlayTrackAsync(Second, cancellationToken: cancellationToken); break;
            case "playlist": await session.PlayPlaylistAsync([Second, First], cancellationToken: cancellationToken); break;
            case "current": await session.PlayCurrentAsync(cancellationToken); break;
            case "queue-item": await session.PlayQueueItemAsync(session.Queue.Items[1].Id, cancellationToken); break;
            case "next": await session.NextAsync(cancellationToken); break;
            case "previous": await session.PreviousAsync(cancellationToken); break;
        }

        Assert.Equal(1, player.Stops);
        Assert.Equal(PlayerState.Playing, session.Snapshot.State);
        Assert.Equal(command == "previous" || command == "current" ? First : Second, player.CurrentTrack);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("stop")]
    [InlineData("source")]
    [InlineData("decoder")]
    public async Task MetadataIsPublishedBeforeSlowPlaybackWorkAndNeverReadsTheOldTimeline(string stage)
    {
        using var paths = new TestPaths();
        var player = new Player();
        var provider = new SourceProvider();
        var repository = new Repository();
        await using var session = new PlaybackSessionService(Resolver(paths, provider, player), new PlaybackQueue(), repository,
            NullLogger<PlaybackSessionService>.Instance);
        var token = TestContext.Current.CancellationToken;
        await session.InitializeAsync(token);
        var second = Second with { Artists = [new ArtistRef { Name = "Second Artist" }], Duration = TimeSpan.FromMinutes(4), ArtworkUri = new("https://artwork.test/second") };
        await session.PlayPlaylistAsync([First, second], cancellationToken: token);
        player.CurrentTimeline = new(TimeSpan.FromSeconds(95), TimeSpan.FromMinutes(3));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Delay(CancellationToken cancellationToken)
        {
            started.TrySetResult();
            return release.Task.WaitAsync(cancellationToken);
        }
        switch (stage)
        {
            case "save": repository.Save = Delay; break;
            case "stop": player.Stop = Delay; break;
            case "source": provider.Resolve = async (_, ct) => { await Delay(ct); return Source; }; break;
            case "decoder": player.Play = Delay; break;
        }
        var changes = new List<PlaybackSessionSnapshot>();
        session.StateChanged += (_, args) => changes.Add(args.Snapshot);
        var switching = session.NextAsync(token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
            player.ThrowOnTimelineRead = true;
            var pending = session.GetCurrentSnapshot();
            Assert.False(switching.IsCompleted);
            Assert.Equal(second, pending.CurrentTrack);
            Assert.Equal(1, pending.CurrentIndex);
            Assert.Equal(TimeSpan.Zero, pending.Timeline.Position);
            Assert.Equal(second.Duration, pending.Timeline.Duration);
            Assert.Equal(PlayerState.Stopped, pending.State);
            Assert.Null(pending.PlayerId);
            Assert.Contains(changes, snapshot => snapshot.CurrentTrack == second && snapshot.State == PlayerState.Stopped);
        }
        finally
        {
            player.ThrowOnTimelineRead = false;
            release.TrySetResult();
            await switching;
        }
        Assert.Equal(second, session.Snapshot.CurrentTrack);
        Assert.Equal(PlayerState.Playing, session.Snapshot.State);
    }

    [AvaloniaFact]
    public async Task DesktopPlaybackBarShowsTheSelectedMetadataWhileTheSourceIsStillResolving()
    {
        using var paths = new TestPaths();
        var provider = new SourceProvider();
        var player = new Player();
        await using var runtime = new DesktopRuntime(paths, new DesktopOptions(Path.Combine(paths.DataDirectory, "plugins"), paths.MusicLibraryDirectory),
            hostPlayers: [new PlayerProvider(player)], hostProviders: [provider]);
        var text = new LocalizationService();
        using var appearance = new AppearanceService(Avalonia.Application.Current!, text, new AppearanceTests.MemoryStore());
        await using var shell = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        await shell.InitializeAsync();
        var session = runtime.Get<PlaybackSessionService>();
        var second = Second with { Artists = [new ArtistRef { Name = "Second Artist" }], Duration = TimeSpan.FromMinutes(4) };
        await session.PlayPlaylistAsync([First, second]);
        shell.Playback.UpdateSnapshot();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Resolve = async (_, ct) => { started.TrySetResult(); await release.Task.WaitAsync(ct); return Source; };
        var switching = shell.Playback.NextCommand.ExecuteAsync(null);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Second", shell.Playback.NowTitle);
            Assert.Equal("Second Artist", shell.Playback.NowArtist);
            Assert.Equal("4:00", shell.Playback.DurationText);
            Assert.Equal(0, shell.Playback.PositionSeconds);
            Assert.False(shell.Playback.IsPlaying);
            var bar = window.FindControl<Border>("PlaybackBar")!;
            Assert.Contains("Second", bar.GetVisualDescendants().OfType<TextBlock>().Select(control => control.Text));
        }
        finally { release.TrySetResult(); await switching; window.Close(); }
        Assert.True(shell.Playback.IsPlaying);
    }

    [Fact]
    public async Task SourceResolutionWaitsUntilThePreviousPlayerHasStopped()
    {
        using var paths = new TestPaths();
        var player = new Player();
        var provider = new SourceProvider();
        var resolver = Resolver(paths, provider, player);
        var cancellationToken = TestContext.Current.CancellationToken;
        await resolver.PlayAsync(First, cancellationToken: cancellationToken);
        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolutionStarted = false;
        player.Stop = async token =>
        {
            stopStarted.SetResult();
            await finishStop.Task.WaitAsync(token);
        };
        provider.Resolve = (_, _) =>
        {
            resolutionStarted = true;
            Assert.Equal(PlayerState.Stopped, player.State);
            return Task.FromResult<PlaybackSource?>(Source);
        };

        var switching = resolver.PlayAsync(Second, cancellationToken: cancellationToken);
        try
        {
            await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            Assert.False(resolutionStarted);
            Assert.False(switching.IsCompleted);
        }
        finally
        {
            finishStop.TrySetResult();
            await switching;
            player.Stop = null;
            await resolver.StopAsync(cancellationToken);
        }
        Assert.True(resolutionStarted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledResolutionLeavesThePreviousTrackStopped(bool cancel)
    {
        using var paths = new TestPaths();
        var player = new Player();
        var provider = new SourceProvider();
        var resolver = Resolver(paths, provider, player);
        var cancellationToken = TestContext.Current.CancellationToken;
        await resolver.PlayAsync(First, cancellationToken: cancellationToken);
        provider.Resolve = (_, _) => Task.FromException<PlaybackSource?>(cancel
            ? new OperationCanceledException()
            : new InvalidOperationException("Source unavailable."));

        if (cancel)
            await Assert.ThrowsAsync<OperationCanceledException>(() => resolver.PlayAsync(Second,
                cancellationToken: cancellationToken));
        else
            await Assert.ThrowsAsync<PlaybackResolutionException>(() => resolver.PlayAsync(Second,
                cancellationToken: cancellationToken));

        Assert.Equal(1, player.Stops);
        Assert.Equal(PlayerState.Stopped, player.State);
        Assert.Null(player.CurrentTrack);
        Assert.Null(resolver.ActivePlayer);
        Assert.Null(resolver.CurrentQueueTrack);
    }

    private static PlaybackResolver Resolver(TestPaths paths, SourceProvider provider, Player player)
    {
        var registry = new ProviderRegistry([provider]);
        return new(registry, registry, new PlayerRegistry([new PlayerProvider(player)]),
            new PlaybackSourceDiscoveryService(registry, new MetadataService(registry)),
            new PlaybackAudioCache(paths.CacheDirectory, NullLogger<PlaybackAudioCache>.Instance));
    }

    private sealed class SourceProvider : IMusicProvider, IPlaybackProvider
    {
        public string Id => "source";
        public ProviderDescriptor Descriptor { get; } = new("Source Fixture", "Resolves test tracks");
        public Func<Track, CancellationToken, Task<PlaybackSource?>> Resolve { get; set; } =
            (_, _) => Task.FromResult<PlaybackSource?>(Source);
        public Task<PlaybackSource?> ResolvePlaybackAsync(Track track, PlaybackRequest request,
            CancellationToken cancellationToken = default) => Resolve(track, cancellationToken);
    }

    private sealed class PlayerProvider(Player player) : IPlayerProvider
    {
        public string Id => "test-player";
        public PlayerDescriptor Descriptor { get; } = new("Test Player", "Records playback transitions");
        public int Priority => 0;
        public IPlayer Player => player;
        public bool CanPlay(PlaybackSource source) => true;
    }

    private sealed class Player : IPlayer
    {
        public PlayerState State { get; private set; }
        public Track? CurrentTrack { get; private set; }
        public PlayerTimeline CurrentTimeline { get; set; } = new(TimeSpan.Zero, null);
        public bool ThrowOnTimelineRead { get; set; }
        public PlayerTimeline Timeline => ThrowOnTimelineRead ? throw new InvalidOperationException("The decoder is busy opening a stream.") : CurrentTimeline;
        public int Stops { get; private set; }
        public Func<CancellationToken, Task>? Stop { get; set; }
        public Func<CancellationToken, Task>? Play { get; set; }
        public async Task PlayAsync(Track track, PlaybackSource source, CancellationToken cancellationToken = default)
        {
            if (Play is not null) await Play(cancellationToken);
            CurrentTrack = track;
            CurrentTimeline = new(TimeSpan.Zero, track.Duration);
            State = PlayerState.Playing;
        }
        public Task PauseAsync(CancellationToken cancellationToken = default)
        {
            State = PlayerState.Paused;
            return Task.CompletedTask;
        }
        public Task ResumeAsync(CancellationToken cancellationToken = default)
        {
            State = PlayerState.Playing;
            return Task.CompletedTask;
        }
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            Stops++;
            if (Stop is not null) await Stop(cancellationToken);
            State = PlayerState.Stopped;
            CurrentTrack = null;
        }
    }

    private sealed class Repository : IPlaybackStateRepository
    {
        public Func<CancellationToken, Task>? Save { get; set; }
        public Task<PlaybackQueueState> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlaybackQueueState());
        public Task SaveAsync(PlaybackQueueState state, CancellationToken cancellationToken = default) => Save?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }
}
