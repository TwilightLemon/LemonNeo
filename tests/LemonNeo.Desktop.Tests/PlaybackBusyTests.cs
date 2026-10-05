using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.ViewModels;
using LemonNeo.Desktop.Views;
using LemonNeo.Plugin.Abstractions;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class PlaybackBusyTests
{
    private static readonly string[] PlaybackButtonNames = ["PlayPauseButton", "PreviousButton", "NextButton", "PlaybackModeButton"];

    [AvaloniaFact]
    public async Task StartupShowsProgressAndPlaybackCanBeCancelledBeforeOrAfterProgressAppears()
    {
        using var paths = new TestPaths();
        await using var runtime = new DesktopRuntime(paths,
            new DesktopOptions(Path.Combine(paths.DataDirectory, "plugins"), paths.MusicLibraryDirectory));
        var text = new LocalizationService();
        using var appearance = new AppearanceService(Avalonia.Application.Current!, text, new AppearanceTests.MemoryStore());
        await using var shell = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        var startupBusy = new List<bool>();
        shell.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(shell.IsBusy)) startupBusy.Add(shell.IsBusy);
        };
        await shell.InitializeAsync();
        Assert.Equal([true, false], startupBusy);

        foreach (var waitForProgress in new[] { false, true })
        {
            var busyShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnPlaybackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(shell.Playback.IsBusy) && shell.Playback.IsBusy) busyShown.TrySetResult();
            }
            shell.Playback.PropertyChanged += OnPlaybackPropertyChanged;
            var operation = shell.Playback.ExecutePlaybackAsync(ct => Task.Delay(Timeout.Infinite, ct));
            try
            {
                Assert.False(shell.Playback.IsBusy);
                Assert.True(shell.Playback.CancelOperationCommand.CanExecute(null));
                if (waitForProgress) await busyShown.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            }
            finally
            {
                shell.Playback.CancelOperationCommand.Execute(null);
                await operation;
                shell.Playback.PropertyChanged -= OnPlaybackPropertyChanged;
            }
            await Task.Delay(300, TestContext.Current.CancellationToken);
            Assert.False(shell.Playback.IsBusy);
            Assert.True(shell.Playback.CanInteract);
            Assert.False(shell.Playback.CancelOperationCommand.CanExecute(null));
            Assert.False(shell.Playback.HasError, shell.Playback.Error);
        }
    }

    [AvaloniaTheory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("seek")]
    [InlineData("stop")]
    [InlineData("clear-queue")]
    public async Task SlowPlaybackControlsStaySilentAndRejectOverlappingOperations(string control)
    {
        using var paths = new TestPaths();
        var player = new Player();
        await using var runtime = new DesktopRuntime(paths,
            new DesktopOptions(Path.Combine(paths.DataDirectory, "plugins"), paths.MusicLibraryDirectory),
            hostPlayers: [new PlayerProvider(player)], hostProviders: [new SourceProvider()]);
        var text = new LocalizationService();
        using var appearance = new AppearanceService(Avalonia.Application.Current!, text, new AppearanceTests.MemoryStore());
        await using var shell = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        await shell.InitializeAsync();
        var session = runtime.Get<PlaybackSessionService>();
        await session.PlayTrackAsync(new Track { Id = new("source", "track"), Title = "Track", Duration = TimeSpan.FromMinutes(3) });
        if (control == "resume") await session.PauseAsync();
        shell.Playback.UpdateSnapshot();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var buttons = PlaybackButtonNames.Select(name => window.FindControl<Button>(name)!).ToArray();
        var enabledBefore = buttons.Select(button => button.IsEffectivelyEnabled).ToArray();
        var opacityBefore = buttons.Select(button => button.Opacity).ToArray();
        player.DelayControls = true;
        var operation = control switch
        {
            "pause" or "resume" => shell.Playback.TogglePlaybackCommand.ExecuteAsync(null),
            "seek" => shell.Playback.SeekAsync(45),
            "stop" => shell.Playback.StopCommand.ExecuteAsync(null),
            "clear-queue" => shell.Playback.ClearQueueCommand.ExecuteAsync(null),
            _ => throw new ArgumentOutOfRangeException(nameof(control))
        };
        try
        {
            await player.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            var overlapRan = false;
            await shell.Playback.ExecutePlaybackAsync(_ => { overlapRan = true; return Task.CompletedTask; });
            Assert.False(overlapRan);
            Assert.True(shell.Playback.CanInteract);
            await Task.Delay(350, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.False(shell.Playback.IsBusy);
            Assert.False(shell.Playback.HasStatus);
            Assert.False(shell.IsBusy);
            Assert.True(window.FindControl<Slider>("Timeline")!.IsVisible);
            Assert.Equal(enabledBefore, buttons.Select(button => button.IsEffectivelyEnabled));
            Assert.Equal(opacityBefore, buttons.Select(button => button.Opacity));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<ProgressBar>(), progress => progress.IsEffectivelyVisible);
        }
        finally
        {
            player.Release.TrySetResult();
            await operation;
            window.Close();
        }
        Assert.True(shell.Playback.CanInteract);
        Assert.False(shell.Playback.HasError, shell.Playback.Error);
        var saved = await runtime.Get<IPlaybackStateRepository>().LoadAsync();
        Assert.Equal(control == "seek" ? TimeSpan.FromSeconds(45) : TimeSpan.Zero, saved.Position);
        Assert.Equal(control switch
        {
            "pause" => PlaybackStatus.Paused,
            "stop" or "clear-queue" => PlaybackStatus.Stopped,
            _ => PlaybackStatus.Playing
        }, saved.Status);
    }

    [AvaloniaTheory]
    [InlineData("play")]
    [InlineData("next")]
    [InlineData("previous")]
    [InlineData("play-all")]
    [InlineData("play-selected")]
    [InlineData("queue-track")]
    public async Task StartingOrSwitchingTracksDoesNotDimPlaybackButtonsOrRebuildThePage(string control)
    {
        using var paths = new TestPaths();
        var player = new Player();
        await using var runtime = new DesktopRuntime(paths,
            new DesktopOptions(Path.Combine(paths.DataDirectory, "plugins"), paths.MusicLibraryDirectory),
            hostPlayers: [new PlayerProvider(player)], hostProviders: [new SourceProvider()]);
        var text = new LocalizationService();
        using var appearance = new AppearanceService(Avalonia.Application.Current!, text, new AppearanceTests.MemoryStore());
        await using var shell = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        await shell.InitializeAsync();
        var playlists = runtime.Get<PlaylistService>();
        var playlist = playlists.Create("Playback fixture");
        for (var i = 0; i < 3; i++)
            playlists.Add(playlist.Id, new Track { Id = new("source", i.ToString()), Title = $"Track {i}", Duration = TimeSpan.FromMinutes(3) });
        shell.SharedContext.RefreshLibrary();
        await shell.Navigation.NavigateAsync(DesktopRoutes.Library, playlist.Id);
        var page = shell.Library;
        page.SelectedTrack = page.Tracks[1];
        var session = runtime.Get<PlaybackSessionService>();
        await session.PlayPlaylistAsync(page.Tracks.Select(row => row.Track), 1);
        if (control == "play") await session.StopAsync();
        shell.Playback.UpdateSnapshot();
        await using var queue = new PlaybackQueueViewModel(shell.SharedContext, shell.Playback);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var view = window.GetVisualDescendants().OfType<LibraryView>().Single();
        var list = view.FindControl<ListBox>("TrackList")!;
        var firstRow = list.ContainerFromIndex(0);
        var selected = page.SelectedTrack;
        var pagePlayButton = view.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.Command, page.PlayAllCommand));
        var buttons = PlaybackButtonNames.Select(name => window.FindControl<Button>(name)!).Append(pagePlayButton).ToArray();
        var enabledBefore = buttons.Select(button => button.IsEffectivelyEnabled).ToArray();
        var opacityBefore = buttons.Select(button => button.Opacity).ToArray();
        player.DelayPlayback = true;
        var operation = control switch
        {
            "play" => shell.Playback.TogglePlaybackCommand.ExecuteAsync(null),
            "next" => shell.Playback.NextCommand.ExecuteAsync(null),
            "previous" => shell.Playback.PreviousCommand.ExecuteAsync(null),
            "play-all" => page.PlayAllCommand.ExecuteAsync(null),
            "play-selected" => page.PlaySelectedCommand.ExecuteAsync(null),
            "queue-track" => queue.PlayTrackCommand.ExecuteAsync(queue.Tracks[0]),
            _ => throw new ArgumentOutOfRangeException(nameof(control))
        };
        try
        {
            await player.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await Task.Delay(350, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(enabledBefore, buttons.Select(button => button.IsEffectivelyEnabled));
            Assert.Equal(opacityBefore, buttons.Select(button => button.Opacity));
            Assert.True(page.PlayAllCommand.CanExecute(null));
            Assert.True(page.PlaySelectedCommand.CanExecute(null));
            Assert.True(queue.PlayTrackCommand.CanExecute(queue.Tracks[0]));
            Assert.True(shell.Playback.IsBusy);
            Assert.False(shell.IsBusy);
            Assert.Same(page, shell.Navigation.Current);
            Assert.Same(view, window.GetVisualDescendants().OfType<LibraryView>().Single());
            Assert.Same(firstRow, list.ContainerFromIndex(0));
            Assert.Same(selected, page.SelectedTrack);
            Assert.True(window.FindControl<Slider>("Timeline")!.IsVisible);
        }
        finally
        {
            player.Release.TrySetResult();
            await operation;
            window.Close();
        }
        Assert.False(shell.Playback.HasError, shell.Playback.Error);
        Assert.Equal(PlayerState.Playing, shell.Playback.Snapshot.State);
    }

    private sealed class SourceProvider : IMusicProvider, IPlaybackProvider
    {
        public string Id => "source";
        public ProviderDescriptor Descriptor { get; } = new("Test Source", "Playback fixture");
        public Task<PlaybackSource?> ResolvePlaybackAsync(Track track, PlaybackRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<PlaybackSource?>(new HttpPlaybackSource { Uri = new("https://audio.test/track.mp3") });
    }

    private sealed class PlayerProvider(Player player) : IPlayerProvider
    {
        public string Id => "test-player";
        public PlayerDescriptor Descriptor { get; } = new("Test Player", "Playback fixture");
        public int Priority => 0;
        public IPlayer Player => player;
        public bool CanPlay(PlaybackSource source) => true;
    }

    private sealed class Player : ISeekablePlayer
    {
        public PlayerState State { get; private set; }
        public Track? CurrentTrack { get; private set; }
        public PlayerTimeline Timeline { get; private set; } = new(TimeSpan.Zero, TimeSpan.FromMinutes(3));
        public bool DelayControls { get; set; }
        public bool DelayPlayback { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task WaitAsync(CancellationToken cancellationToken, bool delay)
        {
            if (!delay) return;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
        public async Task PlayAsync(Track track, PlaybackSource source, CancellationToken cancellationToken = default)
        {
            await WaitAsync(cancellationToken, DelayPlayback);
            CurrentTrack = track;
            State = PlayerState.Playing;
        }
        public async Task PauseAsync(CancellationToken cancellationToken = default)
        {
            await WaitAsync(cancellationToken, DelayControls);
            State = PlayerState.Paused;
        }
        public async Task ResumeAsync(CancellationToken cancellationToken = default)
        {
            await WaitAsync(cancellationToken, DelayControls);
            State = PlayerState.Playing;
        }
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await WaitAsync(cancellationToken, DelayControls);
            State = PlayerState.Stopped;
            CurrentTrack = null;
        }
        public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        {
            await WaitAsync(cancellationToken, DelayControls);
            Timeline = Timeline with { Position = position };
        }
    }
}
