using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Playback;
using LemonNeo.Plugin.Abstractions;
using LemonNeo.Plugin.Runtime;
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

        provider.Resolve = (_, _) =>
        {
            Assert.Equal(PlayerState.Stopped, player.State);
            Assert.Null(resolver.ActivePlayer);
            Assert.Null(resolver.CurrentQueueTrack);
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
        public PlayerTimeline Timeline => new(TimeSpan.Zero, null);
        public int Stops { get; private set; }
        public Func<CancellationToken, Task>? Stop { get; set; }
        public Task PlayAsync(Track track, PlaybackSource source, CancellationToken cancellationToken = default)
        {
            CurrentTrack = track;
            State = PlayerState.Playing;
            return Task.CompletedTask;
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
        public Task<PlaybackQueueState> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlaybackQueueState());
        public Task SaveAsync(PlaybackQueueState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
