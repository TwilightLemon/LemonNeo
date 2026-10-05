using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Desktop.Controls;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.ViewModels;
using LemonNeo.Desktop.Views;
using LemonNeo.Plugin.Abstractions;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class DesktopLyricsTests
{
    private static readonly Lyrics Document = new("first\nsecond\nthird", Lines:
    [
        new("first", 0, 2000, new Dictionary<string, string> { ["zh"] = "第一句" }, "first romanized",
            Words: [new("fi", 0, 1000), new("rst", 1000, 2000)]),
        new("second", 3000, 5000, new Dictionary<string, string> { ["zh"] = "第二句" }, "second romanized"),
        new("third", 6000, 8000)
    ]);

    [AvaloniaFact]
    public void CompactLyricsFollowSeekPauseStopAndTheLastLine()
    {
        var presenter = new LyricsPresenter
        {
            VisibleLineCount = 2, Document = Document, IsStopped = false,
            PositionMilliseconds = 1200, EmptyText = "No track", ReduceMotion = true,
            FontSize = 28, Foreground = Brushes.White, ReachedBrush = Brushes.Gold
        };
        var window = new Window { Width = 600, SizeToContent = SizeToContent.Height, Content = presenter };
        window.Show();
        try
        {
            Assert.Equal(new[] { "first", "second" }, VisibleMainLines(presenter));
            var current = presenter.GetVisualDescendants().OfType<LyricText>().Single(t => t.Text == "first");
            Assert.Equal(Document.Lines![0].Words, current.Words);
            Assert.True(current.IsHighlighted);
            Assert.Equal(1200, current.PositionMilliseconds);
            Assert.True(current.TextLayout.Height <= current.FontSize * 2);
            presenter.PositionMilliseconds = 2400; // Keep completed words across the gap.
            Assert.Equal(new[] { "first", "second" }, VisibleMainLines(presenter));
            presenter.PositionMilliseconds = 3500;
            Assert.Equal(new[] { "second", "third" }, VisibleMainLines(presenter));
            presenter.IsPlaying = false;
            Assert.True(presenter.GetVisualDescendants().OfType<LyricText>().Single(t => t.Text == "second").IsHighlighted);
            presenter.PositionMilliseconds = 1200; // A backwards seek restores the earlier pair.
            Assert.Equal(new[] { "first", "second" }, VisibleMainLines(presenter));
            presenter.VisibleLineCount = 1;
            Assert.Equal(new[] { "first" }, VisibleMainLines(presenter));
            presenter.PositionMilliseconds = 7000;
            presenter.VisibleLineCount = 2;
            Assert.Equal(new[] { "third" }, VisibleMainLines(presenter));
            presenter.IsStopped = true;
            Assert.Empty(VisibleMainLines(presenter));
            Assert.Contains(presenter.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "No track");
            presenter.IsStopped = false;
            Assert.Equal(new[] { "third" }, VisibleMainLines(presenter));
            presenter.Document = null;
            Assert.Empty(VisibleMainLines(presenter));
            Assert.Equal(-1, presenter.CurrentIndex);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AuxiliaryTogglesAndUntimedLyricsWorkWithoutWrappingOrVerticalScrolling()
    {
        var presenter = new LyricsPresenter { VisibleLineCount = 2, Document = Document, IsStopped = false, PositionMilliseconds = 1200 };
        var window = new Window { Width = 350, SizeToContent = SizeToContent.Height, Content = presenter };
        window.Show();
        try
        {
            bool HasVisible(string text) => presenter.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);
            Assert.True(HasVisible("first romanized"));
            Assert.True(HasVisible("第一句"));
            presenter.ShowRomanization = false;
            Assert.False(HasVisible("first romanized"));
            Assert.True(HasVisible("第一句"));
            presenter.ShowTranslation = false;
            Assert.False(HasVisible("第一句"));
            presenter.ShowRomanization = true;
            Assert.True(HasVisible("first romanized"));
            presenter.Document = new Lyrics("A very long lyric that must remain on one physical line even in a narrow window\nNext untimed line\nHidden line");
            Assert.Equal(2, VisibleMainLines(presenter).Length);
            Assert.All(presenter.GetVisualDescendants().OfType<LyricText>(), text => Assert.Equal(TextWrapping.NoWrap, text.TextWrapping));
            Assert.False(presenter.GetVisualDescendants().OfType<ScrollViewer>().Single().Offset.Y > 0);
            presenter.VisibleLineCount = 0; // The original full timeline remains available.
            Assert.Equal(3, VisibleMainLines(presenter).Length);
            Assert.All(presenter.GetVisualDescendants().OfType<LyricText>(), text => Assert.Equal(TextWrapping.Wrap, text.TextWrapping));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WindowToggleKeepsPlaybackCenteredAndClosesWithTheMainWindow()
    {
        using var paths = new TestPaths();
        await using var runtime = new DesktopRuntime(paths, new DesktopOptions(Path.Combine(paths.DataDirectory, "plugins"), paths.MusicLibraryDirectory));
        var text = new LocalizationService();
        var store = new AppearanceTests.MemoryStore();
        using var appearance = new AppearanceService(Avalonia.Application.Current!, text, store);
        await appearance.SetAsync(new UiPreferences(Language: LanguagePreference.Chinese, ReduceMotion: true));
        await using var shell = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        var main = new MainWindow { DataContext = shell };
        main.Show();
        try
        {
            var toggle = main.FindControl<Button>("DesktopLyricsButton")!;
            var previous = main.FindControl<Button>("PreviousButton")!;
            var play = main.FindControl<Button>("PlayPauseButton")!;
            var next = main.FindControl<Button>("NextButton")!;
            var mode = main.FindControl<Button>("PlaybackModeButton")!;
            var left = toggle.TranslatePoint(new Point(toggle.Bounds.Width / 2, 0), main)!.Value.X;
            var right = mode.TranslatePoint(new Point(mode.Bounds.Width / 2, 0), main)!.Value.X;
            var center = play.TranslatePoint(new Point(play.Bounds.Width / 2, 0), main)!.Value.X;
            Assert.Equal(center, (left + right) / 2, precision: 1);
            Assert.True(toggle.TranslatePoint(new Point(toggle.Bounds.Width, 0), main)!.Value.X < previous.TranslatePoint(default, main)!.Value.X);
            Assert.True(next.TranslatePoint(default, main)!.Value.X < right);
            foreach (var width in new[] { 880d, 1180d })
            {
                main.Width = width;
                main.UpdateLayout();
                var now = main.FindControl<Button>("NowPlayingButton")!;
                Assert.True(now.TranslatePoint(new Point(now.Bounds.Width, 0), main)!.Value.X <= toggle.TranslatePoint(default, main)!.Value.X);
            }

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(main.IsDesktopLyricsOpen);
            // Desktop settings use their own persisted values.
            shell.Lyrics.SingleDesktopLineCommand.Execute(null);
            shell.Lyrics.DesktopShowTranslation = false;
            shell.Lyrics.DesktopShowRomanization = false;
            shell.Lyrics.DesktopLyricFontSize = 36;
            Assert.Equal(1, appearance.Current.DesktopLyricLineCount);
            Assert.False(appearance.Current.DesktopShowTranslation);
            Assert.False(appearance.Current.DesktopShowRomanization);
            Assert.True(appearance.Current.ShowTranslation);
            Assert.True(appearance.Current.ShowRomanization);
            Assert.Equal(24, appearance.Current.LyricFontSize);
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(main.IsDesktopLyricsOpen);
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(main.IsDesktopLyricsOpen);
            main.Close();
            Assert.False(main.IsDesktopLyricsOpen);
        }
        finally { main.Close(); }
        using var reloaded = new AppearanceService(Avalonia.Application.Current!, text, store);
        await reloaded.LoadAsync();
        Assert.Equal(1, reloaded.Current.DesktopLyricLineCount);
    }

    [AvaloniaFact]
    public async Task HoverRevealsPlaybackControlsAndSettingsKeepThemVisible()
    {
        using var paths = new TestPaths();
        await using var runtime = new DesktopRuntime(paths, new DesktopOptions(Path.Combine(paths.DataDirectory, "plugins"), paths.MusicLibraryDirectory));
        var text = new LocalizationService();
        using var appearance = new AppearanceService(Avalonia.Application.Current!, text, new AppearanceTests.MemoryStore());
        await appearance.SetAsync(new UiPreferences(Language: LanguagePreference.Chinese, ReduceMotion: true, DesktopLyricFontSize: 28));
        await using var shell = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        var window = new DesktopLyricsWindow { DataContext = shell.Lyrics };
        var presenter = window.FindControl<LyricsPresenter>("DesktopLyricsTimeline")!;
        presenter.Document = Document;
        presenter.IsStopped = false;
        presenter.PositionMilliseconds = 1200;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        try
        {
            Assert.True(window.Topmost);
            Assert.False(window.ShowInTaskbar);
            Assert.False(window.ShowActivated);
            Assert.True(window.CanResize);
            Assert.Equal(SizeToContent.Manual, window.SizeToContent);
            Assert.Equal(2, presenter.VisibleLineCount);
            Assert.InRange(window.Bounds.Height, 100, 350);
            window.UpdateLayout();
            foreach (var line in presenter.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible))
            {
                var bottom = line.TranslatePoint(new Point(0, line.Bounds.Height), window)!.Value.Y;
                Assert.True(bottom <= window.Bounds.Height, $"{line.Text} bottom {bottom} exceeds window {window.Bounds.Height}; desired {window.DesiredSize}, presenter {presenter.Bounds}");
            }
            var toolbar = window.FindControl<Border>("Toolbar")!;
            Assert.Equal(0, toolbar.Opacity);
            window.MouseMove(new Point(80, 60));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, toolbar.Opacity);
            Assert.True(toolbar.IsHitTestVisible);
            Assert.Same(shell.Playback.TogglePlaybackCommand, window.FindControl<Button>("DesktopPlayPauseButton")!.Command);
            Assert.Same(shell.Playback.PreviousCommand, window.FindControl<Button>("DesktopPreviousButton")!.Command);
            Assert.Same(shell.Playback.NextCommand, window.FindControl<Button>("DesktopNextButton")!.Command);
            SavePreview(window, "desktop-hover.png");
            var settings = window.FindControl<Button>("DesktopLyricsSettingsButton")!;
            settings.Focus();
            settings.Flyout!.ShowAt(settings);
            window.MouseMove(new Point(-20, -20));
            Assert.Equal(1, toolbar.Opacity);
            settings.Flyout.Hide();
            Assert.Equal(0, toolbar.Opacity);
            SavePreview(window, "desktop-transparent.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LongLinesScrollLinearlyAndFollowSeekAndWindowWidth()
    {
        var text = string.Concat(Enumerable.Repeat("Long lyrics keep their complete text visible over time. ", 5));
        var presenter = new LyricsPresenter
        {
            VisibleLineCount = 1, IsStopped = false, FontSize = 28, PositionMilliseconds = 1000,
            Document = new Lyrics(text, Lines: [new(text, 1000, 11000), new("Next", 12000, 15000)]),
            ShowRomanization = false, ShowTranslation = false
        };
        var window = new Window { Width = 360, Height = 120, Content = presenter };
        window.Show();
        try
        {
            var viewport = CurrentViewport(presenter);
            var line = Assert.IsType<LyricText>(viewport.Child);
            Assert.Equal(text, line.Text);
            Assert.Equal(TextTrimming.None, line.TextTrimming);
            Assert.Single(line.TextLayout.TextLines);
            var overflow = line.DesiredSize.Width - viewport.Bounds.Width;
            Assert.True(overflow > 500);
            Assert.Equal(0, viewport.HorizontalOffset);
            presenter.PositionMilliseconds = 3500;
            Assert.Equal(overflow * .25, viewport.HorizontalOffset, precision: 1);
            presenter.PositionMilliseconds = 8500;
            Assert.Equal(overflow * .75, viewport.HorizontalOffset, precision: 1);
            presenter.PositionMilliseconds = 11000;
            Assert.Equal(overflow, viewport.HorizontalOffset, precision: 1);
            presenter.PositionMilliseconds = 3500;
            Assert.Equal(overflow * .25, viewport.HorizontalOffset, precision: 1);
            window.Width = 800;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal((line.DesiredSize.Width - viewport.Bounds.Width) * .25, viewport.HorizontalOffset, precision: 1);
            presenter.PositionMilliseconds = 12000;
            Assert.Equal(0, CurrentViewport(presenter).HorizontalOffset);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void WordTimingControlsScrollingAndUpcomingLinesStayAtTheBeginning()
    {
        var tail = string.Concat(Enumerable.Repeat("a long final word group ", 8));
        var words = new LyricsWord[] { new("", 0, 0), new("short ", 0, 9000), new(tail, 9000, 10000) };
        var presenter = new LyricsPresenter
        {
            VisibleLineCount = 2, IsStopped = false, FontSize = 28,
            Document = new Lyrics("short " + tail, Lines: [new("", Words: words), new(tail, 11000, 15000)]),
            ShowRomanization = false, ShowTranslation = false
        };
        var window = new Window { Width = 360, Height = 180, Content = presenter };
        window.Show();
        try
        {
            var viewport = CurrentViewport(presenter);
            presenter.PositionMilliseconds = 5000;
            Assert.Equal(0, viewport.HorizontalOffset); // Half the line's time still belongs to the short first word.
            presenter.PositionMilliseconds = 9500;
            Assert.True(viewport.HorizontalOffset > 100);
            Assert.All(presenter.GetVisualDescendants().OfType<LyricLineViewport>().Where(v => v.IsEffectivelyVisible && !v.IsActive),
                next => Assert.Equal(0, next.HorizontalOffset));
            presenter.PositionMilliseconds = 10000;
            Assert.Equal(viewport.Child!.DesiredSize.Width - viewport.Bounds.Width, viewport.HorizontalOffset, precision: 1);
            presenter.PositionMilliseconds = 5000;
            Assert.Equal(0, viewport.HorizontalOffset);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MissingLineEndsUseTheNextTimestampOrTrackDurationIncludingAuxiliaryText()
    {
        var text = string.Concat(Enumerable.Repeat("Long main and auxiliary lyrics ", 8));
        var presenter = new LyricsPresenter
        {
            VisibleLineCount = 1, IsStopped = false, FontSize = 28, DurationMilliseconds = 21000,
            Document = new Lyrics(text, Lines:
            [
                new(text, 1000, Romanization: text, Translations: new Dictionary<string, string> { ["zh"] = text }),
                new(text, 11000)
            ])
        };
        var window = new Window { Width = 360, Height = 200, Content = presenter };
        window.Show();
        try
        {
            presenter.PositionMilliseconds = 6000;
            foreach (var viewport in presenter.GetVisualDescendants().OfType<LyricLineViewport>().Where(v => v.IsEffectivelyVisible))
                Assert.Equal((viewport.Child!.DesiredSize.Width - viewport.Bounds.Width) * .5, viewport.HorizontalOffset, precision: 1);
            presenter.PositionMilliseconds = 16000;
            var last = CurrentViewport(presenter);
            Assert.Equal((last.Child!.DesiredSize.Width - last.Bounds.Width) * .5, last.HorizontalOffset, precision: 1);
            presenter.DurationMilliseconds = 31000;
            Assert.Equal((last.Child.DesiredSize.Width - last.Bounds.Width) * .25, last.HorizontalOffset, precision: 1);
            presenter.Document = new Lyrics(text);
            presenter.DurationMilliseconds = 10000;
            presenter.PositionMilliseconds = 5000;
            window.UpdateLayout();
            var untimed = CurrentViewport(presenter);
            Assert.Equal((untimed.Child!.DesiredSize.Width - untimed.Bounds.Width) * .5, untimed.HorizontalOffset, precision: 1);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PlaybackClockMovesLongLinesAndPauseFreezesThem()
    {
        var text = string.Concat(Enumerable.Repeat("Scrolling follows playback ", 6));
        var presenter = new LyricsPresenter
        {
            VisibleLineCount = 1, IsStopped = false, IsPlaying = true, ReduceMotion = true, FontSize = 28,
            Document = new Lyrics(text, Lines: [new(text, 0, 10000)]), PositionMilliseconds = 5000
        };
        var window = new Window { Width = 360, Height = 120, Content = presenter };
        window.Show();
        try
        {
            var viewport = CurrentViewport(presenter);
            var offset = viewport.HorizontalOffset;
            await Task.Delay(120, TestContext.Current.CancellationToken);
            Assert.True(viewport.HorizontalOffset > offset);
            presenter.IsPlaying = false;
            var paused = viewport.HorizontalOffset;
            await Task.Delay(80, TestContext.Current.CancellationToken);
            Assert.Equal(paused, viewport.HorizontalOffset);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    public void RenderingFramesAdvanceLyricsWithoutWaitingForAFixedInterval(int visibleLineCount)
    {
        var presenter = new LyricsPresenter
        {
            VisibleLineCount = visibleLineCount, IsStopped = false, IsPlaying = true,
            Document = new Lyrics("karaoke", Lines: [new("karaoke", 0, 10000, Words: [new("karaoke", 0, 10000)])])
        };
        var window = new Window { Width = 240, Height = 120, Content = presenter };
        window.Show();
        try
        {
            var lyric = presenter.GetVisualDescendants().OfType<LyricText>().Single(t => t.Text == "karaoke");
            for (var frame = 0; frame < 8; frame++)
            {
                var previous = lyric.PositionMilliseconds;
                var frameRendered = false;
                window.RequestAnimationFrame(_ => frameRendered = true);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                Assert.True(frameRendered);
                Assert.True(lyric.PositionMilliseconds > previous);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RenderingResumesAfterAnAncestorIsShownOrTheControlChangesWindows()
    {
        var text = string.Concat(Enumerable.Repeat("Scrolling follows rendering ", 6));
        var presenter = new LyricsPresenter
        {
            VisibleLineCount = 1, IsStopped = false, IsPlaying = true, ReduceMotion = true, FontSize = 28,
            Document = new Lyrics(text, Lines: [new(text, 0, 10000)]), PositionMilliseconds = 5000
        };
        var parent = new Border { Child = presenter };
        var first = new Window { Width = 360, Height = 120, Content = parent };
        var second = new Window { Width = 360, Height = 120 };
        first.Show();
        second.Show();
        try
        {
            var viewport = CurrentViewport(presenter);
            var initial = viewport.HorizontalOffset;
            await Task.Delay(80, TestContext.Current.CancellationToken);
            Assert.True(viewport.HorizontalOffset > initial);

            parent.IsVisible = false;
            var hidden = viewport.HorizontalOffset;
            await Task.Delay(80, TestContext.Current.CancellationToken);
            Assert.Equal(hidden, viewport.HorizontalOffset);
            parent.IsVisible = true;
            await Task.Delay(80, TestContext.Current.CancellationToken);
            Assert.True(viewport.HorizontalOffset > hidden);

            // Both a callback from the old window and one from the new window can be queued.
            parent.Child = null;
            var detached = viewport.HorizontalOffset;
            await Task.Delay(80, TestContext.Current.CancellationToken);
            Assert.Equal(detached, viewport.HorizontalOffset);
            second.Content = presenter;
            second.UpdateLayout();
            var reattached = viewport.HorizontalOffset;
            await Task.Delay(80, TestContext.Current.CancellationToken);
            Assert.True(viewport.HorizontalOffset > reattached);

            second.WindowState = WindowState.Minimized;
            var minimized = viewport.HorizontalOffset;
            await Task.Delay(80, TestContext.Current.CancellationToken);
            Assert.Equal(minimized, viewport.HorizontalOffset);
            second.WindowState = WindowState.Normal;
            await Task.Delay(80, TestContext.Current.CancellationToken);
            Assert.True(viewport.HorizontalOffset > minimized);
            presenter.IsStopped = true;
            Assert.Empty(VisibleMainLines(presenter));
        }
        finally { first.Close(); second.Close(); }
    }

    [AvaloniaFact]
    public async Task PausedFullTimelineCanStillAnimateFollowingTheCurrentLine()
    {
        var presenter = new LyricsPresenter
        {
            IsStopped = false, IsPlaying = false, FontSize = 28, PositionMilliseconds = 20000,
            ShowRomanization = false, ShowTranslation = false,
            Document = new Lyrics("timeline", Lines: Enumerable.Range(0, 30)
                .Select(index => new LyricsLine($"Line {index}", index * 1000, (index + 1) * 1000)).ToArray())
        };
        var window = new Window { Width = 360, Height = 240, Content = presenter };
        window.Show();
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var scroll = presenter.GetVisualDescendants().OfType<ScrollViewer>().Single();
            var target = scroll.Offset.Y;
            Assert.True(target > 100);
            scroll.Offset = default;
            presenter.FollowCurrentLine();
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.True(scroll.Offset.Y > 0);
            await Task.Delay(450, TestContext.Current.CancellationToken);
            Assert.Equal(target, scroll.Offset.Y, precision: 1);
            Assert.Equal(20000, presenter.GetVisualDescendants().OfType<LyricText>().Single(t => t.Text == "Line 20").PositionMilliseconds);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DesktopPreferencesAndResizedWindowPersistIndependentlyOfMainLyrics()
    {
        using var paths = new TestPaths();
        await using var runtime = new DesktopRuntime(paths, new DesktopOptions(Path.Combine(paths.DataDirectory, "plugins"), paths.MusicLibraryDirectory));
        var text = new LocalizationService();
        var store = new AppearanceTests.MemoryStore();
        using var appearance = new AppearanceService(Avalonia.Application.Current!, text, store);
        await appearance.SetAsync(new UiPreferences(ReduceMotion: true));
        await using var shell = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        var vm = shell.Lyrics;
        vm.DesktopLyricFontSize = 36;
        vm.DesktopShowTranslation = false;
        vm.DesktopShowRomanization = false;
        Assert.Equal(24, vm.LyricFontSize);
        Assert.True(vm.ShowTranslation);
        Assert.True(vm.ShowRomanization);
        vm.LyricFontSize = 20;
        vm.ShowTranslation = false;
        vm.ShowRomanization = false;
        vm.DesktopShowTranslation = true;
        vm.DesktopShowRomanization = true;
        Assert.Equal(36, vm.DesktopLyricFontSize);
        Assert.False(vm.ShowTranslation);
        Assert.False(vm.ShowRomanization);
        var window = new DesktopLyricsWindow { DataContext = vm };
        window.Show();
        try
        {
            window.Width = 540;
            window.Height = 360;
            Dispatcher.UIThread.RunJobs();
            vm.LyricFontSize = 40;
            vm.DesktopLyricFontSize = 32;
            window.UpdateLayout();
            Assert.Equal(540, window.Width);
            Assert.Equal(360, window.Height);
            window.MouseMove(new Point(2, 60));
            Assert.NotNull(window.Cursor); // Borderless edges expose a resize cursor.
            window.MouseMove(new Point(100, 60));
            Assert.Null(window.Cursor);
        }
        finally { window.Close(); }
        using var reloaded = new AppearanceService(Avalonia.Application.Current!, text, store);
        await reloaded.LoadAsync();
        Assert.Equal(32, reloaded.Current.DesktopLyricFontSize);
        Assert.Equal(40, reloaded.Current.LyricFontSize);
        Assert.True(reloaded.Current.DesktopShowTranslation);
        Assert.True(reloaded.Current.DesktopShowRomanization);
        Assert.False(reloaded.Current.ShowTranslation);
        Assert.False(reloaded.Current.ShowRomanization);
        Assert.Equal(540, reloaded.Current.DesktopLyricWindowWidth);
        Assert.Equal(360, reloaded.Current.DesktopLyricWindowHeight);
        var reopened = new DesktopLyricsWindow { DataContext = vm };
        Assert.Equal(540, reopened.Width);
        Assert.Equal(360, reopened.Height);
        reopened.Close();
    }

    [AvaloniaFact]
    public void BorderDraggingResizesTheWindowAndKeepsTheOppositeEdgeFixed()
    {
        var window = new DesktopLyricsWindow { Width = 480, Height = 260 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var position = window.Position;
            window.MouseDown(new Point(2, 130), MouseButton.Left);
            window.MouseMove(new Point(42, 130));
            window.MouseUp(new Point(42, 130), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(440, window.Bounds.Width);
            Assert.Equal(position.X + 40, window.Position.X);
            Assert.Equal(position.Y, window.Position.Y);

            window.MouseDown(new Point(438, 258), MouseButton.Left);
            window.MouseMove(new Point(578, 338));
            window.MouseUp(new Point(578, 338), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(580, window.Bounds.Width);
            Assert.Equal(340, window.Bounds.Height);
            Assert.Equal(position.X + 40, window.Position.X);

            window.MouseDown(new Point(2, 2), MouseButton.Left);
            window.MouseMove(new Point(1002, 1002));
            window.MouseUp(new Point(1002, 1002), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(window.MinWidth, window.Bounds.Width);
            Assert.Equal(window.MinHeight, window.Bounds.Height);
        }
        finally { window.Close(); }
    }

    private static LyricLineViewport CurrentViewport(LyricsPresenter presenter) => presenter.GetVisualDescendants()
        .OfType<LyricLineViewport>().Single(v => v.IsEffectivelyVisible && v.IsActive && v.Child is LyricText text && text.FontSize == presenter.FontSize);

    private static string[] VisibleMainLines(LyricsPresenter presenter) => presenter.GetVisualDescendants()
        .OfType<LyricText>().Where(t => t.IsEffectivelyVisible && t.FontSize == presenter.FontSize).Select(t => t.Text!).ToArray();

    private static void SavePreview(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("LEMONNEO_LYRICS_PREVIEW_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
    }
}
