using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Desktop.Controls;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.ViewModels;
using LemonNeo.Desktop.Views;
using SkiaSharp;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class PlaybackBarTests
{
    [AvaloniaFact]
    public void GlassBlursThePageAndRefreshesWhenTheBackdropChanges()
    {
        var stripes = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < 50; i++)
            stripes.Children.Add(new Border { Width = 4, Background = i % 2 == 0 ? Brushes.Black : Brushes.White });
        var glass = new FrostedGlass { Margin = new Thickness(20), CornerRadius = new CornerRadius(16), Background = Brushes.Transparent };
        var window = new Window
        {
            Width = 200, Height = 100, Background = Brushes.Black,
            Content = new Grid { Children = { stripes, glass } }
        };
        window.Show();
        try
        {
            using var first = Capture(window);
            Assert.True(Math.Abs(first.GetPixel(40, 10).Red - first.GetPixel(44, 10).Red) > 200);
            Assert.InRange(first.GetPixel(40, 50).Red, (byte)90, (byte)165);
            Assert.True(Math.Abs(first.GetPixel(40, 50).Red - first.GetPixel(44, 50).Red) < 30);

            foreach (var stripe in stripes.Children.OfType<Border>()) stripe.Background = Brushes.Lime;
            using var changed = Capture(window);
            Assert.True(changed.GetPixel(100, 50).Green > 240);
            Assert.True(changed.GetPixel(100, 50).Red < 15);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1180, 790, ThemePreference.Light)]
    [InlineData(880, 620, ThemePreference.Dark)]
    public async Task ContentExtendsBehindTheCardAndTheLastTrackCanScrollAboveIt(int width, int height, ThemePreference theme)
    {
        using var paths = new TestPaths();
        await using var runtime = new DesktopRuntime(paths,
            new DesktopOptions(Path.Combine(paths.DataDirectory, "plugins"), paths.MusicLibraryDirectory));
        var text = new LocalizationService();
        using var appearance = new AppearanceService(Avalonia.Application.Current!, text, new AppearanceTests.MemoryStore());
        await appearance.SetAsync(new UiPreferences(Theme: theme, Language: LanguagePreference.Chinese, ReduceMotion: true));
        await using var shell = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        await shell.InitializeAsync();
        var playlists = runtime.Get<PlaylistService>();
        var playlist = playlists.Create("Floating player preview");
        for (var i = 0; i < 40; i++)
            playlists.Add(playlist.Id, new Track
            {
                Id = new("test", i.ToString()), Title = $"Track {i + 1:00}", Duration = TimeSpan.FromSeconds(180 + i),
                Artists = [new ArtistRef { Name = "Lemon Neo" }]
            });
        shell.SharedContext.RefreshLibrary();
        var window = new MainWindow { Width = width, Height = height, DataContext = shell };
        window.Show();
        try
        {
            await shell.Navigation.NavigateAsync(DesktopRoutes.Library, playlist.Id);
            using var initial = Capture(window);
            var root = window.FindControl<Grid>("ShellRoot")!;
            var page = window.FindControl<ContentControl>("PageContent")!;
            var bar = window.FindControl<Border>("PlaybackBar")!;
            var barTop = bar.TranslatePoint(default, root)!.Value.Y;
            Assert.Equal(root.Bounds.Height, page.TranslatePoint(default, root)!.Value.Y + page.Bounds.Height, precision: 1);
            Assert.True(page.Bounds.Height > bar.Bounds.Height);
            Assert.True(bar.Bounds.Right < root.Bounds.Width);
            foreach (var name in new[] { "NowPlayingButton", "PreviousButton", "PlayPauseButton", "NextButton", "PlaybackModeButton", "VolumeButton", "QueueButton" })
            {
                var button = window.FindControl<Button>(name)!;
                var position = button.TranslatePoint(default, bar)!.Value;
                Assert.True(position.X >= 0 && position.X + button.Bounds.Width <= bar.Bounds.Width, name);
                Assert.True(position.Y >= 0 && position.Y + button.Bounds.Height <= bar.Bounds.Height, name);
            }
            var previewDirectory = Environment.GetEnvironmentVariable("LEMONNEO_PLAYBACK_PREVIEW_DIR");
            if (!string.IsNullOrEmpty(previewDirectory))
            {
                Directory.CreateDirectory(previewDirectory);
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(previewDirectory, $"player-{width}-{theme}.png"), PngBitmapEncoderOptions.Default);
            }

            var finishLoading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var busyShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnPlaybackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(shell.Playback.IsBusy) && shell.Playback.IsBusy) busyShown.TrySetResult();
            }
            shell.Playback.PropertyChanged += OnPlaybackPropertyChanged;
            var loading = shell.Playback.ExecutePlaybackAsync(_ => finishLoading.Task);
            try
            {
                Assert.False(shell.Playback.IsBusy);
                Assert.True(shell.Playback.CanInteract);
                await busyShown.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
                using var loadingFrame = Capture(window);
                Assert.True(window.FindControl<Slider>("Timeline")!.IsVisible);
                Assert.True(window.FindControl<ProgressBar>("PlaybackLoading")!.IsVisible);
                Assert.False(shell.IsBusy);
            }
            finally
            {
                shell.Playback.PropertyChanged -= OnPlaybackPropertyChanged;
                finishLoading.TrySetResult();
                await loading;
            }
            using var loadedFrame = Capture(window);
            Assert.True(window.FindControl<Slider>("Timeline")!.IsVisible);
            Assert.False(window.FindControl<ProgressBar>("PlaybackLoading")!.IsVisible);

            var busyChanges = 0;
            void CountBusyChanges(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(shell.Playback.IsBusy)) busyChanges++;
            }
            shell.Playback.PropertyChanged += CountBusyChanges;
            try
            {
                await shell.Playback.ExecutePlaybackAsync(_ => Task.CompletedTask);
                await Task.Delay(300, TestContext.Current.CancellationToken);
                Assert.Equal(0, busyChanges);
                Assert.True(shell.Playback.CanInteract);
            }
            finally { shell.Playback.PropertyChanged -= CountBusyChanges; }

            await shell.Playback.ExecutePlaybackAsync(_ => Task.FromException(new InvalidOperationException("Source unavailable")));
            using var failedFrame = Capture(window);
            Assert.True(shell.Playback.HasError);
            Assert.True(window.FindControl<Slider>("Timeline")!.IsVisible);
            Assert.False(window.FindControl<ProgressBar>("PlaybackLoading")!.IsVisible);
            // Error text can grow the card; the scroll clearance must grow with it.
            barTop = bar.TranslatePoint(default, root)!.Value.Y;
            var view = page.GetVisualDescendants().OfType<LibraryView>().Single();
            var list = view.FindControl<ListBox>("TrackList")!;
            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().Single();
            scroll.ScrollToEnd();
            using var scrolled = Capture(window);
            var last = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(39));
            Assert.True(last.TranslatePoint(default, root)!.Value.Y + last.Bounds.Height <= barTop - 8);

            await shell.Navigation.NavigateAsync(DesktopRoutes.Settings);
            using var settings = Capture(window);
            var settingsScroll = page.GetVisualDescendants().OfType<SettingsView>().Single()
                .GetVisualDescendants().OfType<ScrollViewer>().Single();
            settingsScroll.ScrollToEnd();
            using var settingsEnd = Capture(window);
            var settingsPanel = Assert.IsType<StackPanel>(settingsScroll.Content);
            var finalSection = settingsPanel.Children.Last();
            var finalBottom = finalSection.TranslatePoint(default, root)!.Value.Y + finalSection.Bounds.Height;
            Assert.True(finalBottom <= barTop - 8,
                $"Bottom={finalBottom}, bar={barTop}, padding={settingsScroll.Padding}, extent={settingsScroll.Extent}, viewport={settingsScroll.Viewport}, offset={settingsScroll.Offset}");
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1180, 790)]
    [InlineData(880, 620)]
    public void MainContentFadesToTheCanvasAtTheBottomAndKeepsItsFadeDistanceAfterResizing(int width, int height)
    {
        var window = new MainWindow { Width = width, Height = height };
        window.Resources["Brush.Canvas"] = Brushes.Black;
        var page = window.FindControl<ContentControl>("PageContent")!;
        page.Content = new Border { Background = Brushes.White };
        var bar = window.FindControl<Border>("PlaybackBar")!;
        bar.IsVisible = false;
        window.Show();
        try
        {
            void AssertFade()
            {
                using var frame = Capture(window);
                byte Brightness(double distanceFromBottom)
                {
                    var point = page.TranslatePoint(new Point(page.Bounds.Width / 2, page.Bounds.Height - distanceFromBottom), window)!.Value;
                    return frame.GetPixel((int)point.X, (int)point.Y).Red;
                }
                Assert.InRange(Brightness(80), (byte)245, (byte)255);
                Assert.InRange(Brightness(32), (byte)112, (byte)144);
                Assert.InRange(Brightness(2), (byte)0, (byte)16);
                Assert.True(page.IsEffectivelyVisible);
                Assert.True(page.IsHitTestVisible);
                Assert.Null(bar.OpacityMask);
                Assert.Null(window.FindControl<Border>("Sidebar")!.OpacityMask);
            }
            AssertFade();
            window.Height += 140;
            AssertFade();
        }
        finally { window.Close(); }
    }

    private static SKBitmap Capture(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var stream = new MemoryStream();
        frame.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        return SKBitmap.Decode(stream);
    }
}
