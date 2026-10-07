using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Application;
using LemonNeo.Application.Playback;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using LemonNeo.Desktop;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Shell;
using LemonNeo.Desktop.Views.Shell;
using Xunit;

namespace LemonNeo.Hosting.Tests;

public sealed class DesktopPlaybackSourceDetailsTests
{
    [AvaloniaFact]
    public async Task DecoderMeasurementsOverrideProviderLabelsAndDrivePlaybackDisplayAndCache()
    {
        using var paths = new TemporaryPaths();
        var provider = new SourceProvider(); // Claims HiRes and 1536 kbps.
        var player = new MeasuredTestPlayer { Measurements = new(845, 44100, 16, 2, true, "FLAC") };
        await using var host = new DesktopHost(paths, new(paths.PluginDirectory, paths.MusicLibraryDirectory), [player], [provider]);
        var scope = host.Settings.ForPackage(PackageNames.Desktop);
        DesktopPreferences.Register(scope);
        using var text = new LocalizationService(scope);
        text.Apply(LanguagePreference.English);
        using var appearance = new AppearanceService(Avalonia.Application.Current!, scope);
        await using var main = new MainViewModel(host, text, appearance, host.Get<ArtworkService>());
        await main.InitializeAsync();
        var session = host.Get<PlaybackSessionService>();
        var id = new ExternalMusicId("audio-catalog", "actual-id", "song");
        await session.PlayTrackAsync(HostFixture.Track() with { ExternalIds = [id] }, cancellationToken: TestContext.Current.CancellationToken);
        main.Playback.UpdateSnapshot();

        Assert.Equal("Lossless", main.Playback.QualityLabel);
        Assert.Equal("845 kbps", main.Playback.SourceBitrate);
        Assert.Equal("Bitrate (decoder)", main.Playback.BitrateOrigin);
        Assert.Equal("FLAC", main.Playback.SourceCodec);
        Assert.Equal("44.1 kHz", main.Playback.SourceSampleRate);
        Assert.Equal("16 bit", main.Playback.SourceBitDepth);
        Assert.Equal("2", main.Playback.SourceChannels);
        Assert.Equal("Lossless · FLAC · 845 kbps · 44.1 kHz · 16 bit", main.Playback.AudioFormatLabel);
        Assert.Equal(player.Measurements, session.Snapshot.AudioInfo);
        Assert.Equal(AudioQuality.Lossless, session.Snapshot.Quality);
        var cache = host.Get<PlaybackAudioCache>();
        Assert.Null(await cache.TryGetAsync(id, AudioQuality.HiRes, TestContext.Current.CancellationToken));
        var cached = await cache.TryGetAsync(id, AudioQuality.Lossless, TestContext.Current.CancellationToken);
        Assert.NotNull(cached);
        Assert.Equal(845, cached.BitrateKbps);

        await session.PauseAsync(TestContext.Current.CancellationToken);
        main.Playback.UpdateSnapshot();
        Assert.Equal("845 kbps", main.Playback.SourceBitrate);
        player.Measurements = new(256, 48000, null, 2, false, "MP3");
        await session.PlayTrackAsync(HostFixture.Track("second") with { ExternalIds = [id with { Value = "second" }] },
            cancellationToken: TestContext.Current.CancellationToken);
        main.Playback.UpdateSnapshot();
        Assert.Equal("High", main.Playback.QualityLabel);
        Assert.Equal("256 kbps", main.Playback.SourceBitrate);
        Assert.DoesNotContain("FLAC", main.Playback.AudioFormatLabel);
        Assert.DoesNotContain("16 bit", main.Playback.AudioFormatLabel);

        await session.StopAsync(TestContext.Current.CancellationToken);
        main.Playback.UpdateSnapshot();
        Assert.Null(session.Snapshot.AudioInfo);
        Assert.Equal("", main.Playback.AudioFormatLabel);
    }

    [AvaloniaFact]
    public async Task QualityButtonShowsSelectedSourceAndFollowsPauseStopAndCachedPlayback()
    {
        using var paths = new TemporaryPaths();
        var provider = new SourceProvider();
        await using var host = new DesktopHost(paths, new(paths.PluginDirectory, paths.MusicLibraryDirectory), [new TestPlayer()], [provider]);
        var scope = host.Settings.ForPackage(PackageNames.Desktop);
        DesktopPreferences.Register(scope);
        using var text = new LocalizationService(scope);
        text.Apply(LanguagePreference.English);
        using var appearance = new AppearanceService(Avalonia.Application.Current!, scope);
        await using var main = new MainViewModel(host, text, appearance, host.Get<ArtworkService>());
        await main.InitializeAsync();
        var session = host.Get<PlaybackSessionService>();
        var id = new ExternalMusicId("audio-catalog", "actual-id", "song");
        var track = HostFixture.Track() with { ExternalIds = [id] };
        var window = new MainWindow { DataContext = main };
        window.Show();
        try
        {
            var button = window.FindControl<Button>("CurrentAudioQuality")!;
            var popup = window.FindControl<Popup>("SourceDetailsPopup")!;
            var location = window.FindControl<SelectableTextBlock>("PlaybackSourceLocation")!;
            Assert.False(button.IsEnabled);
            await session.PlayTrackAsync(track, cancellationToken: TestContext.Current.CancellationToken);
            main.Playback.UpdateSnapshot();
            Dispatcher.UIThread.RunJobs();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(popup.IsOpen);
            Assert.Equal(provider.Id, main.Playback.SourceProvider);
            Assert.Equal("audio-catalog", main.Playback.SourceNamespace);
            Assert.Equal("song", main.Playback.SourceKind);
            Assert.Equal("actual-id", main.Playback.SourceId);
            Assert.Equal("Hi-Res", main.Playback.QualityLabel);
            Assert.Equal("1536 kbps", main.Playback.SourceBitrate);
            Assert.Equal("Online audio", main.Playback.SourceType);
            Assert.Equal(provider.Source.Uri.AbsoluteUri, location.Text);
            Assert.Equal("acceptance-player", main.Playback.SourcePlayer);

            await session.PauseAsync(TestContext.Current.CancellationToken);
            main.Playback.UpdateSnapshot();
            Assert.True(popup.IsOpen);
            Assert.Equal(provider.Source.Uri.AbsoluteUri, location.Text);
            window.FindControl<Border>("SourceDetailsCard")!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.False(popup.IsOpen);
            Assert.False(main.Playback.IsSourceDetailsOpen);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(popup.IsOpen);
            await session.StopAsync(TestContext.Current.CancellationToken);
            main.Playback.UpdateSnapshot();
            Assert.False(popup.IsOpen);
            Assert.False(button.IsEnabled);
            Assert.Null(session.Snapshot.SourceDetails);

            var cache = host.Get<PlaybackAudioCache>();
            using (var writer = cache.Start(id, provider.Source))
            {
                Assert.NotNull(writer);
                writer.SetExpectedLength(4);
                writer.Write([1, 2, 3, 4]);
                writer.Complete();
            }
            await session.PlayTrackAsync(track, cancellationToken: TestContext.Current.CancellationToken);
            main.Playback.UpdateSnapshot();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(popup.IsOpen);
            Assert.Equal(1, provider.Calls);
            Assert.True(session.Snapshot.SourceDetails!.IsCached);
            Assert.Equal("Cached audio", main.Playback.SourceType);
            Assert.Equal("Unknown", main.Playback.SourceProvider);
            Assert.Equal(id.Value, main.Playback.SourceId);
            Assert.Equal("1536 kbps", main.Playback.SourceBitrate);
            Assert.True(File.Exists(location.Text));
            window.PrepareForBackground();
            Assert.False(popup.IsOpen);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ChangingTracksReplacesSourceDetailsAndShowsUnknownBitrate()
    {
        using var paths = new TemporaryPaths();
        var provider = new SourceProvider();
        await using var host = new DesktopHost(paths, new(paths.PluginDirectory, paths.MusicLibraryDirectory), [new TestPlayer()], [provider]);
        var scope = host.Settings.ForPackage(PackageNames.Desktop);
        DesktopPreferences.Register(scope);
        using var text = new LocalizationService(scope);
        text.Apply(LanguagePreference.English);
        using var appearance = new AppearanceService(Avalonia.Application.Current!, scope);
        await using var main = new MainViewModel(host, text, appearance, host.Get<ArtworkService>());
        await main.InitializeAsync();
        var session = host.Get<PlaybackSessionService>();
        var track = HostFixture.Track() with { ExternalIds = [new("audio-catalog", "first", "song")] };
        await session.PlayTrackAsync(track, cancellationToken: TestContext.Current.CancellationToken);
        main.Playback.UpdateSnapshot();
        main.Playback.IsSourceDetailsOpen = true;
        var selectedWithoutSource = false;
        session.StateChanged += (_, args) =>
        {
            if (args.Snapshot.CurrentTrack?.Title != "second" || args.Snapshot.SourceDetails is not null) return;
            selectedWithoutSource = true;
            main.Playback.UpdateSnapshot();
            Assert.False(main.Playback.IsSourceDetailsOpen);
        };
        provider.Source = provider.Source with { Uri = new("https://example.com/second.flac"), BitrateKbps = null };
        await session.PlayTrackAsync(track with { Id = new("original", "second"), Title = "second", ExternalIds = [new("audio-catalog", "second", "song")] },
            cancellationToken: TestContext.Current.CancellationToken);
        main.Playback.UpdateSnapshot();

        Assert.True(selectedWithoutSource);
        Assert.Equal("second", main.Playback.SourceId);
        Assert.Equal(provider.Source.Uri.AbsoluteUri, main.Playback.SourceLocation);
        Assert.Equal("Unknown", main.Playback.SourceBitrate);
        text.Apply(LanguagePreference.Chinese);
        Assert.Equal("未知", main.Playback.SourceBitrate);
        Assert.Equal("在线音频", main.Playback.SourceType);
    }

    private sealed class SourceProvider : IMusicProvider, IPlaybackProvider
    {
        public string Id => "source-provider";
        public ProviderDescriptor Descriptor { get; } = new("Test source", "Resolves a separate catalog identity.");
        public IReadOnlyCollection<MusicIdScheme> AcceptedIdSchemes => [new("audio-catalog", "song")];
        public HttpPlaybackSource Source { get; set; } = new()
        {
            Uri = new("https://example.com/audio.flac"), Quality = AudioQuality.HiRes, BitrateKbps = 1536
        };
        public int Calls { get; private set; }
        public Task<PlaybackSource?> ResolvePlaybackAsync(MusicItemId trackId, PlaybackRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<PlaybackSource?>(Source);
        }
    }
}
