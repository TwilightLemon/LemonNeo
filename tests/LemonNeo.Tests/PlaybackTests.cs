using System.Collections.Concurrent;
using LemonNeo.Abstractions.Playback;
using LemonNeo.Application.Lyrics;
using LemonNeo.Application.Playback;
using LemonNeo.Core.Exceptions;
using LemonNeo.Core.Lyrics;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using LemonNeo.Desktop.Services;
using Xunit;
using static LemonNeo.Tests.PlaybackFixture;

namespace LemonNeo.Tests;

public sealed class PlaybackTests
{
    [Fact]
    public void ListMatchingUsesKnownIdsWithoutAResolvedSourceAndRespectsIdKinds()
    {
        var current = Track("A") with { ExternalIds = [new ExternalMusicId("qqmusic", "123", "song-id")] };
        var row = Track("B") with { ExternalIds = [new ExternalMusicId("QQMUSIC", "123", "SONG-ID")] };
        Assert.True(PlayingTrackMatcher.Matches(row, current));
        Assert.False(PlayingTrackMatcher.Matches(row with { ExternalIds = [new ExternalMusicId("qqmusic", "123", "song-mid")] }, current));
    }

    [Fact]
    public async Task SelectionAndSystemMetadataArePublishedBeforeSourceResolution()
    {
        await using var fixture = new PlaybackFixture();
        await fixture.InitializeAsync();
        var release = Signal<PlaybackSource?>();
        var entered = Signal<bool>();
        fixture.Provider.Resolve = async (_, token) => { entered.TrySetResult(true); return await release.Task.WaitAsync(token); };
        var published = Signal<InteractivePlaybackSnapshot>();
        fixture.Interaction.MetadataChanged += (_, args) => published.TrySetResult(args.Snapshot);
        var completion = fixture.Session.PlayTrackAsync(Track("A"));
        Assert.Equal("A", fixture.Session.Snapshot.CurrentTrack?.Title);
        Assert.Equal(PlaybackState.Loading, fixture.Session.Snapshot.State);
        Assert.Equal(PlaybackState.Stopped, fixture.Settings.Get<PlaybackResumeState>().Status);
        Assert.Null(fixture.Session.Snapshot.SourceDetails);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var metadata = await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("A", metadata.Track?.Title);
        Assert.Equal(PlaybackState.Loading, metadata.State);
        Assert.Empty(fixture.Provider.Lyrics);
        Assert.True(PlayingTrackMatcher.Matches(Track("A"), fixture.Session.Snapshot.CurrentTrack));
        await fixture.Session.StopAsync();
        await completion;
    }

    [Fact]
    public async Task NewSelectionCancelsSourceSearchAndStartsWithoutWaiting()
    {
        await using var fixture = new PlaybackFixture();
        await fixture.InitializeAsync();
        var entered = Signal<CancellationToken>();
        fixture.Provider.Resolve = async (id, token) =>
        {
            if (id.RemoteId == "A") { entered.TrySetResult(token); await Task.Delay(Timeout.Infinite, token); }
            return new HttpPlaybackSource { Uri = new Uri("https://test.invalid/audio"), Quality = AudioQuality.HiRes };
        };
        var old = fixture.Session.PlayTrackAsync(Track("A"));
        var oldToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Session.PlayTrackAsync(Track("B")).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(oldToken.IsCancellationRequested);
        await old;
        Assert.Equal("B", fixture.Session.Snapshot.CurrentTrack?.Title);
        Assert.Equal(new[] { "B" }, fixture.Player.Activations);
    }

    [Fact]
    public async Task LatePreparationCannotActivateOrStopTheNewStream()
    {
        await using var fixture = new PlaybackFixture();
        await fixture.InitializeAsync();
        var entered = Signal<bool>();
        var release = Signal<IPreparedPlayback>();
        var abandoned = new TestPlayer.TestPrepared(fixture.Player, Track("A"));
        fixture.Player.Prepare = (track, _) =>
        {
            if (track.Title == "A") { entered.TrySetResult(true); return release.Task; }
            return Task.FromResult<IPreparedPlayback>(new TestPlayer.TestPrepared(fixture.Player, track));
        };
        var old = fixture.Session.PlayTrackAsync(Track("A"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Session.PlayTrackAsync(Track("B"));
        release.SetResult(abandoned);
        await old;
        Assert.True(abandoned.Disposed);
        Assert.Equal("B", fixture.Player.CurrentTrack?.Title);
        Assert.Equal(PlaybackState.Playing, fixture.Session.Snapshot.State);
        Assert.Equal(new[] { "B" }, fixture.Player.Activations);
    }

    [Fact]
    public async Task ExplicitFailureKeepsSelectionAndDoesNotSkipOrLoadLyrics()
    {
        await using var fixture = new PlaybackFixture();
        await fixture.InitializeAsync();
        fixture.Provider.Resolve = (_, _) => Task.FromResult<PlaybackSource?>(null);
        await Assert.ThrowsAsync<PlaybackSourceUnavailableException>(() => fixture.Session.PlayPlaylistAsync([Track("A"), Track("B")]));
        Assert.Equal(PlaybackState.Failed, fixture.Session.Snapshot.State);
        Assert.Equal(PlaybackState.Stopped, fixture.Settings.Get<PlaybackResumeState>().Status);
        Assert.Equal("A", fixture.Session.Snapshot.CurrentTrack?.Title);
        Assert.True(PlayingTrackMatcher.Matches(Track("A"), fixture.Session.Snapshot.CurrentTrack));
        Assert.All(fixture.Provider.Sources, id => Assert.Equal("A", id));
        Assert.Empty(fixture.Provider.Lyrics);
    }

    [Theory]
    [InlineData(PlaybackMode.Sequential)]
    [InlineData(PlaybackMode.RepeatAll)]
    public async Task AutomaticAdvanceSkipsUnavailableTracks(PlaybackMode mode)
    {
        await using var fixture = new PlaybackFixture();
        await fixture.InitializeAsync();
        fixture.Provider.Resolve = (id, _) => Task.FromResult<PlaybackSource?>(id.RemoteId == "B" ? null :
            new HttpPlaybackSource { Uri = new Uri("https://test.invalid/audio"), Quality = AudioQuality.HiRes });
        await fixture.Session.SetModeAsync(mode);
        await fixture.Session.PlayPlaylistAsync([Track("A"), Track("B"), Track("C")]);
        fixture.Player.Timeline = fixture.Player.Timeline with { HasEnded = true };
        await UntilAsync(() => fixture.Session.Snapshot.CurrentTrack?.Title == "C" && fixture.Session.Snapshot.State == PlaybackState.Playing);
        Assert.Equal(new[] { "A", "C" }, fixture.Player.Activations);
        Assert.Contains("B", fixture.Provider.Sources);
    }

    [Theory]
    [InlineData(PlaybackMode.RepeatAll)]
    [InlineData(PlaybackMode.Shuffle)]
    public async Task AutomaticAdvanceStopsAfterOneFinitePassWhenEverythingIsUnavailable(PlaybackMode mode)
    {
        await using var fixture = new PlaybackFixture();
        await fixture.InitializeAsync();
        await fixture.Session.SetModeAsync(mode);
        await fixture.Session.PlayPlaylistAsync([Track("A"), Track("B"), Track("C")]);
        fixture.Provider.Resolve = (_, _) => Task.FromResult<PlaybackSource?>(null);
        fixture.Player.Timeline = fixture.Player.Timeline with { HasEnded = true };
        await UntilAsync(() => fixture.Session.Snapshot.State == PlaybackState.Failed);
        var sources = fixture.Provider.Sources.ToArray().Skip(1).ToArray();
        Assert.Equal(3, sources.Distinct().Count());
        Assert.Equal(15, sources.Length); // Five qualities for each unavailable identity, one attempt per queue item.
        Assert.Single(fixture.Player.Activations);
    }

    [Fact]
    public async Task SystemMediaStopCanInterruptAQueuedNextCommand()
    {
        await using var fixture = new PlaybackFixture();
        await fixture.InitializeAsync();
        await fixture.Session.PlayPlaylistAsync([Track("A"), Track("B")]);
        var entered = Signal<CancellationToken>();
        fixture.Provider.Resolve = async (_, token) => { entered.TrySetResult(token); await Task.Delay(Timeout.Infinite, token); return null; };
        await fixture.Interaction.ExecuteAsync(PlaybackCommand.Next).WaitAsync(TimeSpan.FromSeconds(1));
        var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Interaction.ExecuteAsync(PlaybackCommand.Stop).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(PlaybackState.Stopped, fixture.Session.Snapshot.State);
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task LyricsLoadAfterActivationAndDoNotReloadOnPauseSeekOrResume()
    {
        await using var fixture = new PlaybackFixture();
        await fixture.InitializeAsync();
        var entered = Signal<bool>();
        var release = Signal<IPreparedPlayback>();
        fixture.Player.Prepare = (track, _) => { entered.TrySetResult(true); return release.Task; };
        var playing = fixture.Session.PlayTrackAsync(Track("A"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(fixture.Provider.Lyrics);
        release.SetResult(new TestPlayer.TestPrepared(fixture.Player, Track("A")));
        await playing;
        await UntilAsync(() => fixture.Lyrics.Snapshot.Status == PlaybackLyricsStatus.Loaded);
        await fixture.Session.PauseAsync();
        await fixture.Session.SeekAsync(TimeSpan.FromSeconds(5));
        await fixture.Session.ResumeAsync();
        Assert.Equal(new[] { "A" }, fixture.Provider.Lyrics);
        Assert.Equal("A", fixture.Lyrics.Snapshot.Document?.Text);
    }

    [Fact]
    public async Task ReplayingTheSameTrackRejectsAnOlderLyricsResult()
    {
        await using var fixture = new PlaybackFixture();
        await fixture.InitializeAsync();
        var firstEntered = Signal<bool>();
        var firstResult = Signal<Lyrics?>();
        var count = 0;
        fixture.Provider.FetchLyrics = (_, _) => Interlocked.Increment(ref count) == 1 ? First() : Task.FromResult<Lyrics?>(new Lyrics("new"));
        async Task<Lyrics?> First() { firstEntered.TrySetResult(true); return await firstResult.Task; }
        await fixture.Session.PlayTrackAsync(Track("A"));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var firstId = fixture.Session.Snapshot.RequestId;
        await fixture.Session.PlayTrackAsync(Track("A"));
        await UntilAsync(() => fixture.Lyrics.Snapshot.Document?.Text == "new");
        firstResult.SetResult(new Lyrics("old"));
        Assert.NotEqual(firstId, fixture.Session.Snapshot.RequestId);
        Assert.Equal("new", fixture.Lyrics.Snapshot.Document?.Text);
    }

    [Fact]
    public async Task TimelineAndVolumeNotificationsDoNotRepublishMetadata()
    {
        await using var fixture = new PlaybackFixture();
        await fixture.InitializeAsync();
        var events = new ConcurrentQueue<(string Kind, long Revision)>();
        fixture.Session.MetadataChanged += (_, args) => events.Enqueue(("metadata", args.Snapshot.Revision));
        fixture.Session.TimelineChanged += (_, args) => events.Enqueue(("timeline", args.Snapshot.Revision));
        fixture.Session.VolumeChanged += (_, args) => events.Enqueue(("volume", args.Snapshot.Revision));
        await fixture.Session.PlayTrackAsync(Track("A"));
        await fixture.Session.PauseAsync();
        await fixture.Session.SeekAsync(TimeSpan.FromSeconds(12));
        var seekRevision = fixture.Session.Snapshot.Revision;
        await UntilAsync(() => events.Any(item => item.Kind == "timeline" && item.Revision >= seekRevision));
        await fixture.Session.SetVolumeAsync(0.25);
        var volumeRevision = fixture.Session.Snapshot.Revision;
        await UntilAsync(() => events.Any(item => item.Kind == "volume" && item.Revision >= volumeRevision));
        Assert.DoesNotContain(events, item => item.Kind == "metadata" && item.Revision >= seekRevision);
        Assert.Equal(0.25, fixture.Player.Volume);
    }

    [Fact]
    public async Task DuplicateQueueTracksHaveAnExactCurrentItemIdentity()
    {
        await using var fixture = new PlaybackFixture();
        await fixture.InitializeAsync();
        await fixture.Session.PlayPlaylistAsync([Track("A"), Track("A")], 1);
        Assert.Equal(fixture.Session.Queue.Items[1].Id, fixture.Session.Snapshot.CurrentQueueItemId);
        Assert.NotEqual(fixture.Session.Queue.Items[0].Id, fixture.Session.Snapshot.CurrentQueueItemId);
    }

    [Fact]
    public async Task HostConfigurationFailureDoesNotSkipTheRestOfTheQueue()
    {
        await using var fixture = new PlaybackFixture();
        await fixture.InitializeAsync();
        await fixture.Session.PlayPlaylistAsync([Track("A"), Track("B"), Track("C")]);
        fixture.Registry.HasDecoder = false;
        fixture.Player.Timeline = fixture.Player.Timeline with { HasEnded = true };
        await UntilAsync(() => fixture.Session.Snapshot.State == PlaybackState.Failed);
        Assert.Equal("B", fixture.Session.Snapshot.CurrentTrack?.Title);
        Assert.DoesNotContain("C", fixture.Provider.Sources);
        Assert.Contains("No decoder", fixture.Session.Snapshot.Error);
    }

    [Fact]
    public async Task UserSelectionReplacesAnAutomaticAdvance()
    {
        await using var fixture = new PlaybackFixture();
        await fixture.InitializeAsync();
        await fixture.Session.PlayPlaylistAsync([Track("A"), Track("B"), Track("C")]);
        var entered = Signal<CancellationToken>();
        var release = Signal<PlaybackSource?>();
        fixture.Provider.Resolve = (id, token) =>
        {
            if (id.RemoteId == "B") { entered.TrySetResult(token); return release.Task; }
            return Task.FromResult<PlaybackSource?>(new HttpPlaybackSource { Uri = new Uri("https://test.invalid/audio"), Quality = AudioQuality.HiRes });
        };
        fixture.Player.Timeline = fixture.Player.Timeline with { HasEnded = true };
        var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Session.PlayTrackAsync(Track("D"));
        release.TrySetResult(null);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal("D", fixture.Session.Snapshot.CurrentTrack?.Title);
        Assert.Equal(new[] { "A", "D" }, fixture.Player.Activations);
        Assert.DoesNotContain("C", fixture.Provider.Sources);
    }

    [Fact]
    public async Task StopDuringDecoderPreparationDisposesItsLateResult()
    {
        await using var fixture = new PlaybackFixture();
        await fixture.InitializeAsync();
        var entered = Signal<bool>();
        var release = Signal<IPreparedPlayback>();
        fixture.Player.Prepare = (_, _) => { entered.TrySetResult(true); return release.Task; };
        var request = fixture.Session.PlayTrackAsync(Track("A"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Session.StopAsync();
        var prepared = new TestPlayer.TestPrepared(fixture.Player, Track("A"));
        release.TrySetResult(prepared);
        await request;
        Assert.True(prepared.Disposed);
        Assert.Empty(fixture.Player.Activations);
        Assert.Equal(PlaybackState.Stopped, fixture.Session.Snapshot.State);
        Assert.Equal("A", fixture.Session.Snapshot.CurrentTrack?.Title);
    }

    [Fact]
    public async Task AFailedRestoreKeepsItsSavedPositionForRetry()
    {
        await using var fixture = new PlaybackFixture();
        var item = new PlaybackQueueItem { Track = Track("A") };
        fixture.Settings.Register(nameof(PlaybackQueueState), new PlaybackQueueState { Items = [item], CurrentItemId = item.Id });
        fixture.Settings.Register(nameof(PlaybackResumeState), new PlaybackResumeState(item.Id, TimeSpan.FromSeconds(45), PlaybackState.Paused));
        await fixture.InitializeAsync();
        fixture.Provider.Resolve = (_, _) => Task.FromResult<PlaybackSource?>(null);
        await Assert.ThrowsAsync<PlaybackSourceUnavailableException>(() => fixture.Session.ResumeAsync());
        Assert.Equal(TimeSpan.FromSeconds(45), fixture.Settings.Get<PlaybackResumeState>().Position);
        fixture.Provider.Resolve = null;
        await fixture.Session.ResumeAsync();
        Assert.Equal(TimeSpan.FromSeconds(45), fixture.Player.Timeline.Position);
        Assert.Equal(PlaybackState.Playing, fixture.Session.Snapshot.State);
    }

    [Fact]
    public async Task CancellingTheCallerStopsItsActiveRequest()
    {
        await using var fixture = new PlaybackFixture();
        await fixture.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        await fixture.Session.PlayTrackAsync(Track("A"), cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await UntilAsync(() => fixture.Session.Snapshot.State == PlaybackState.Stopped);
        Assert.Equal("A", fixture.Session.Snapshot.CurrentTrack?.Title);
        Assert.Null(fixture.Session.Snapshot.Error);
    }
}
