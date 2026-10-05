using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.ViewModels;
using LemonNeo.Desktop.Views;
using LemonNeo.Plugin.Abstractions;
using Xunit;
using Popup = Avalonia.Controls.Primitives.Popup;

namespace LemonNeo.Desktop.Tests;

public sealed class PlaybackVolumeTests
{
    [AvaloniaFact]
    public async Task PopupSliderAdjustsDecoderVolumeWithoutBusyOrDimmingPlaybackControls()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Session.SetVolumeAsync(.45);
        await fixture.Session.PlayTrackAsync(Track("first"));
        fixture.Shell.Playback.UpdateSnapshot();
        var window = new MainWindow { DataContext = fixture.Shell };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var button = window.FindControl<Button>("VolumeButton")!;
            var queue = window.FindControl<Button>("QueueButton")!;
            Assert.True(button.IsEffectivelyEnabled);
            Assert.True(button.TranslatePoint(default, window)!.Value.X + button.Bounds.Width <= queue.TranslatePoint(default, window)!.Value.X);
            Click(window, button);
            var popup = window.FindControl<Popup>("VolumePopup")!;
            var slider = window.FindControl<Slider>("VolumeSlider")!;
            Assert.True(popup.IsOpen);
            Assert.Equal(0, slider.Minimum);
            Assert.Equal(100, slider.Maximum);
            await fixture.Shell.Playback.RefreshVolumeAsync();
            Assert.Equal(45, slider.Value);
            var buttons = new[] { "PlayPauseButton", "PreviousButton", "NextButton", "VolumeButton", "QueueButton" }
                .Select(name => window.FindControl<Button>(name)!).ToArray();
            var enabled = buttons.Select(control => control.IsEffectivelyEnabled).ToArray();
            var opacity = buttons.Select(control => control.Opacity).ToArray();
            var busyEvents = 0;
            fixture.Shell.Playback.PropertyChanged += (_, args) => { if (args.PropertyName == "IsBusy") busyEvents++; };
            fixture.Player.DelayVolume = true;
            slider.Value = 20;
            await fixture.Player.VolumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            slider.Value = 70;
            slider.Value = 0;
            await Task.Delay(300, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(enabled, buttons.Select(control => control.IsEffectivelyEnabled));
            Assert.Equal(opacity, buttons.Select(control => control.Opacity));
            Assert.False(fixture.Shell.Playback.IsBusy);
            Assert.False(fixture.Shell.IsBusy);
            Assert.False(window.FindControl<ProgressBar>("PlaybackLoading")!.IsVisible);
            Assert.True(fixture.Shell.Playback.IsMuted);
            Assert.Equal("0%", fixture.Shell.Playback.VolumeLabel);
            fixture.Player.VolumeRelease.TrySetResult();
            await WaitUntilAsync(() => fixture.Session.Volume == 0);
            Assert.Equal(0, await fixture.Session.GetVolumeAsync());
            Assert.Equal(0, (await fixture.Runtime.Get<IPlaybackStateRepository>().LoadAsync()).Volume);
            Assert.Equal(0, busyEvents);
            Assert.False(fixture.Shell.Playback.HasError, fixture.Shell.Playback.Error);

            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.False(popup.IsOpen);
            Assert.True(button.IsFocused);
            Click(window, button);
            Assert.True(popup.IsOpen);
            fixture.Shell.ToggleQueueCommand.Execute(null);
            Assert.False(popup.IsOpen);
            Assert.True(fixture.Shell.IsQueueOpen);
            fixture.Shell.CloseQueueCommand.Execute(null);
            Click(window, button);
            Assert.True(popup.IsOpen);
            window.MouseDown(new Point(400, 200), MouseButton.Left);
            window.MouseUp(new Point(400, 200), MouseButton.Left);
            Assert.False(popup.IsOpen);
        }
        finally { fixture.Player.VolumeRelease.TrySetResult(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task VolumeIsAppliedBeforeStartingEveryTrackAndRestoredAfterRestart()
    {
        using var paths = new TestPaths();
        await using (var fixture = await Fixture.CreateAsync(paths))
        {
            await fixture.Session.SetVolumeAsync(.32);
            Assert.Equal(.32, await fixture.Session.GetVolumeAsync());
            await fixture.Session.PlayPlaylistAsync([Track("first"), Track("second")]);
            Assert.Equal(.32, fixture.Player.VolumeAtPlay);
            await fixture.Session.NextAsync();
            Assert.Equal(.32, fixture.Player.VolumeAtPlay);
            await fixture.Session.PauseAsync();
            await fixture.Session.ResumeAsync();
            await fixture.Session.StopAsync();
            await fixture.Session.PlayTrackAsync(Track("third"));
            Assert.Equal(.32, fixture.Player.VolumeAtPlay);
            await fixture.Session.ClearQueueAsync();
            Assert.Equal(.32, (await fixture.Runtime.Get<IPlaybackStateRepository>().LoadAsync()).Volume);
        }
        await using var restored = await Fixture.CreateAsync(paths);
        Assert.Equal(32, restored.Shell.Playback.VolumePercent);
        await restored.Session.PlayTrackAsync(Track("restored"));
        Assert.Equal(.32, restored.Player.VolumeAtPlay);
    }

    [AvaloniaFact]
    public async Task OpeningThePopupReadsVolumeFromTheActiveDecoder()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Session.PlayTrackAsync(Track("first"));
        fixture.Player.Volume = .18;
        await fixture.Shell.Playback.RefreshVolumeAsync();
        Assert.Equal(18, fixture.Shell.Playback.VolumePercent);
        Assert.Equal(.18, fixture.Session.Volume);
        await fixture.Session.NextAsync();
        Assert.Equal(.18, (await fixture.Runtime.Get<IPlaybackStateRepository>().LoadAsync()).Volume);
    }

    [AvaloniaFact]
    public async Task ShutdownFlushesTheLatestSliderValueWhileADecoderUpdateIsPending()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Session.PlayTrackAsync(Track("first"));
        fixture.Player.DelayVolume = true;
        fixture.Shell.Playback.VolumePercent = 25;
        await fixture.Player.VolumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        fixture.Shell.Playback.VolumePercent = 65;
        fixture.Shell.Playback.VolumePercent = 37;
        var shutdown = fixture.DisposeShellAsync();
        try
        {
            Assert.False(shutdown.IsCompleted);
            fixture.Shell.Playback.VolumePercent = 99;
        }
        finally { fixture.Player.VolumeRelease.TrySetResult(); await shutdown; }
        Assert.Equal(.37, fixture.Player.Volume);
        Assert.Equal(.37, (await fixture.Runtime.Get<IPlaybackStateRepository>().LoadAsync()).Volume);
    }

    [AvaloniaFact]
    public async Task DecoderFailureShowsAnErrorAndAllowsTheNextVolumeChange()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Session.PlayTrackAsync(Track("first"));
        fixture.Player.FailVolume = true;
        fixture.Shell.Playback.VolumePercent = 25;
        await WaitUntilAsync(() => fixture.Shell.Playback.HasError && fixture.Shell.Playback.VolumePercent == 100);
        Assert.Contains("volume unavailable", fixture.Shell.Playback.Error);
        Assert.False(fixture.Shell.Playback.IsBusy);
        fixture.Player.FailVolume = false;
        fixture.Shell.Playback.VolumePercent = 40;
        await WaitUntilAsync(() => fixture.Session.Volume == .4);
        Assert.False(fixture.Shell.Playback.HasError, fixture.Shell.Playback.Error);
    }

    [AvaloniaFact]
    public async Task PlayersWithoutTheOptionalCapabilityCanStillPlay()
    {
        await using var fixture = await Fixture.CreateAsync(legacy: true);
        Assert.False(fixture.Shell.Playback.CanAdjustVolume);
        fixture.Shell.Playback.VolumePercent = 20;
        Assert.Equal(100, fixture.Shell.Playback.VolumePercent);
        await fixture.Session.PlayTrackAsync(Track("legacy"));
        Assert.Equal(PlayerState.Playing, fixture.Session.Snapshot.State);
        Assert.False(fixture.Shell.Playback.CanAdjustVolume);
        var window = new MainWindow { DataContext = fixture.Shell };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.FindControl<Button>("VolumeButton")!.IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(-.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task VolumeRejectsValuesOutsideTheNormalizedRange(double volume)
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Session.SetVolumeAsync(volume));
        Assert.Equal(1, fixture.Session.Volume);
    }

    [AvaloniaTheory]
    [InlineData("{}", 1)]
    [InlineData("{\"volume\":-5}", 0)]
    [InlineData("{\"volume\":5}", 1)]
    [InlineData("{\"volume\":0}", 0)]
    public async Task RestoringOldOrOutOfRangeStateUsesAValidVolume(string json, double expected)
    {
        using var paths = new TestPaths();
        Directory.CreateDirectory(paths.MusicLibraryDirectory);
        await File.WriteAllTextAsync(Path.Combine(paths.MusicLibraryDirectory, "playback.json"), json, TestContext.Current.CancellationToken);
        await using var fixture = await Fixture.CreateAsync(paths);
        Assert.Equal(expected * 100, fixture.Shell.Playback.VolumePercent);
        await fixture.Session.PlayTrackAsync(Track("restored"));
        Assert.Equal(expected, fixture.Player.VolumeAtPlay);
    }

    private static Track Track(string id) => new() { Id = new("source", id), Title = id, Duration = TimeSpan.FromMinutes(3) };

    private static void Click(Window window, Button button)
    {
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed class Fixture(TestPaths paths, bool ownsPaths, DesktopRuntime runtime, AppearanceService appearance, MainViewModel shell, VolumePlayer player) : IAsyncDisposable
    {
        private bool _shellDisposed;
        public DesktopRuntime Runtime => runtime;
        public MainViewModel Shell => shell;
        public VolumePlayer Player => player;
        public PlaybackSessionService Session => runtime.Get<PlaybackSessionService>();
        public Task DisposeShellAsync()
        {
            _shellDisposed = true;
            return shell.DisposeAsync().AsTask();
        }
        public static async Task<Fixture> CreateAsync(TestPaths? paths = null, bool legacy = false)
        {
            var ownsPaths = paths is null;
            paths ??= new TestPaths();
            var player = new VolumePlayer();
            var runtime = new DesktopRuntime(paths, new DesktopOptions(Path.Combine(paths.DataDirectory, "plugins"), paths.MusicLibraryDirectory),
                hostPlayers: [new PlayerProvider(legacy ? new LegacyPlayer() : player)], hostProviders: [new SourceProvider()]);
            var text = new LocalizationService();
            var appearance = new AppearanceService(Avalonia.Application.Current!, text, new AppearanceTests.MemoryStore());
            var shell = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
            await shell.InitializeAsync();
            return new(paths, ownsPaths, runtime, appearance, shell, player);
        }
        public async ValueTask DisposeAsync()
        {
            player.VolumeRelease.TrySetResult();
            if (!_shellDisposed) await DisposeShellAsync();
            appearance.Dispose();
            await runtime.DisposeAsync();
            if (ownsPaths) paths.Dispose();
        }
    }

    private sealed class SourceProvider : IMusicProvider, IPlaybackProvider
    {
        public string Id => "source";
        public ProviderDescriptor Descriptor { get; } = new("Test Source", "Volume fixture");
        public Task<PlaybackSource?> ResolvePlaybackAsync(Track track, PlaybackRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<PlaybackSource?>(new HttpPlaybackSource { Uri = new("https://audio.test/track.mp3") });
    }

    private sealed class PlayerProvider(IPlayer player) : IPlayerProvider
    {
        public string Id => "test-player";
        public PlayerDescriptor Descriptor { get; } = new("Test Player", "Volume fixture");
        public int Priority => 0;
        public IPlayer Player => player;
        public bool CanPlay(PlaybackSource source) => true;
    }

    private class LegacyPlayer : IPlayer
    {
        public PlayerState State { get; private set; }
        public Track? CurrentTrack { get; private set; }
        public PlayerTimeline Timeline { get; } = new(TimeSpan.Zero, TimeSpan.FromMinutes(3));
        public virtual Task PlayAsync(Track track, PlaybackSource source, CancellationToken cancellationToken = default)
        { CurrentTrack = track; State = PlayerState.Playing; return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken cancellationToken = default) { State = PlayerState.Paused; return Task.CompletedTask; }
        public Task ResumeAsync(CancellationToken cancellationToken = default) { State = PlayerState.Playing; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { State = PlayerState.Stopped; CurrentTrack = null; return Task.CompletedTask; }
    }

    private sealed class VolumePlayer : LegacyPlayer, IVolumePlayer
    {
        public double Volume { get; set; } = 1;
        public double VolumeAtPlay { get; private set; }
        public bool DelayVolume { get; set; }
        public bool FailVolume { get; set; }
        public TaskCompletionSource VolumeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource VolumeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<double> GetVolumeAsync(CancellationToken cancellationToken = default) => Task.FromResult(Volume);
        public async Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
        {
            if (FailVolume) throw new InvalidOperationException("volume unavailable");
            if (DelayVolume)
            {
                VolumeStarted.TrySetResult();
                await VolumeRelease.Task.WaitAsync(cancellationToken);
            }
            Volume = volume;
        }
        public override Task PlayAsync(Track track, PlaybackSource source, CancellationToken cancellationToken = default)
        { VolumeAtPlay = Volume; return base.PlayAsync(track, source, cancellationToken); }
    }
}
