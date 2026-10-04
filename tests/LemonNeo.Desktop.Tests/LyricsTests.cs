using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Configuration;
using LemonNeo.Application;
using LemonNeo.Desktop.Controls;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Plugin.Abstractions;
using CommunityToolkit.Mvvm.Input;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class LyricsTests
{
    [Fact]
    public void FullDocumentPreservesAuxiliaryTimelinesAndUntimedFallbacks()
    {
        var lyrics = new Lyrics("hello world\nDone", Lines:
            [new("Done", 4000, 5000), new("hello world", 0, 3000, Words: [new("hello ", 0, 1000), new("world", 1000, 2500)])],
            RomanizationLines: [new([new("he ", 0, 1000), new("lo", 1000, 3000)])],
            TranslationLines: [new("你好", 500, 1500), new("世界", 1500, 3000)]);
        var rows = LyricsDocument.Create(lyrics);
        Assert.Equal(["hello world", "Done"], rows.Select(r => r.Main.Text));
        Assert.Equal(2, rows[0].Main.Words!.Count);
        Assert.Equal(2, Assert.Single(rows[0].Romanization).Words!.Count);
        Assert.Equal("世界", LyricDocumentRow.SelectAuxiliary(rows[0].Translation, 1600)!.Text);
        Assert.Empty(rows[1].Romanization); Assert.Empty(rows[1].Translation);

        var plain = LyricsDocument.Create(new Lyrics("One\nTwo", Romanization: "wan\ntu", Translation: "一\n二"));
        Assert.Equal("tu", Assert.Single(plain[1].Romanization).Text);
        Assert.Equal("二", Assert.Single(plain[1].Translation).Text);
        Assert.Empty(LyricsDocument.Create(new Lyrics("One", Romanization: "[00:00]wan", Translation: "[00:00]一"))[0].Romanization);
    }

    [AvaloniaFact]
    public void WordHighlightRendersAProgressiveGradientWithoutRebuildingTextLayout()
    {
        var text = new LyricText
        {
            Words = [new("MMMMMMMM ", 0, 1000), new("MMMMMMMM", 1000, 2000)],
            IsHighlighted = true, FontSize = 38, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, Foreground = Brushes.Gray, ReachedBrush = Brushes.Red
        };
        var window = new Window { Content = text, Width = 240, Height = 260, Background = Brushes.Black };
        window.Show();
        try
        {
            window.UpdateLayout();
            var layout = text.TextLayout;
            Assert.True(layout.TextLines.Count > 1);
            var before = CountRedPixels(window, text, -1);
            var early = CountRedPixels(window, text, 250);
            var late = CountRedPixels(window, text, 750);
            var firstComplete = CountRedPixels(window, text, 1000);
            var allComplete = CountRedPixels(window, text, 2000);
            Assert.True(early > before && late > early && firstComplete > late && allComplete > firstComplete,
                $"Highlight did not sweep progressively: {before}, {early}, {late}, {firstComplete}, {allComplete}");
            Assert.Same(layout, text.TextLayout);
            Assert.Equal(early, CountRedPixels(window, text, 250)); // Seeking backward restores the exact highlight.
            text.ReduceMotion = true;
            Assert.Equal(firstComplete, CountRedPixels(window, text, 250));
            text.IsHighlighted = false;
            Assert.Equal(before, CountRedPixels(window, text, 2000));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TimelineScrollsSmoothlyAllowsBrowsingAndFreezesWhenPaused()
    {
        var control = CreatePresenter();
        var window = new Window { Content = control, Width = 440, Height = 400 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var scroll = Assert.Single(control.GetVisualDescendants().OfType<ScrollViewer>());
            var texts = control.GetVisualDescendants().OfType<LyricText>().ToArray();
            Assert.Equal(24, texts.Length);
            Assert.Equal(0, control.CurrentIndex);
            var initial = scroll.Offset.Y;
            control.PositionMilliseconds = 1100;
            Assert.Equal(1, control.CurrentIndex);
            Assert.Equal(initial, scroll.Offset.Y);
            await PumpAsync(180);
            var middle = scroll.Offset.Y;
            Assert.True(middle > initial);
            await PumpAsync(400);
            Assert.True(scroll.Offset.Y > middle);

            // A wheel gesture suspends automatic following while highlighting continues.
            window.MouseWheel(new Point(200, 160), new Vector(0, -1));
            Assert.False(control.IsFollowing);
            var manual = scroll.Offset.Y;
            control.PositionMilliseconds = 2100;
            await PumpAsync(550);
            Assert.Equal(manual, scroll.Offset.Y);
            control.FollowCurrentLine();
            await PumpAsync(550);
            Assert.True(control.IsFollowing);
            Assert.NotEqual(manual, scroll.Offset.Y);

            var current = Assert.Single(texts, t => t.IsHighlighted && t.FontSize == control.FontSize);
            var pausedPosition = current.PositionMilliseconds;
            await PumpAsync(100);
            Assert.Equal(pausedPosition, current.PositionMilliseconds);
            control.IsPlaying = true;
            await PumpAsync(100);
            Assert.True(current.PositionMilliseconds > pausedPosition);
            control.IsPlaying = false;
            var frozen = current.PositionMilliseconds;
            await PumpAsync(100);
            Assert.Equal(frozen, current.PositionMilliseconds);

            control.PositionMilliseconds = 100;
            Assert.Equal(0, control.CurrentIndex);
            control.IsStopped = true;
            Assert.Equal(-1, control.CurrentIndex);
            Assert.DoesNotContain(texts, t => t.IsHighlighted);
            control.Document = new Lyrics("A new song", Lines: [new("A new song", 0, 1000)]);
            Assert.Single(control.GetVisualDescendants().OfType<LyricText>(), t => !string.IsNullOrEmpty(t.Text));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void HighlightRemainsThroughGapsUntilTheNextLineStarts()
    {
        var control = new LyricsPresenter
        {
            Document = new Lyrics("Word lyric\nLine lyric\nLast lyric", Lines:
                [new("Word lyric", 1000, 1500, Words: [new("Word ", 1000, 1250), new("lyric", 1250, 1500)]),
                 new("Line lyric", 3000, 3400), new("Last lyric", 5000, 5400)],
                RomanizationLines: [new([new("word ", 800, 1050), new("lyric", 1050, 1300)]),
                    new([], "line lyric", 3000, 3400)]),
            IsStopped = false, FontSize = 24, Foreground = Brushes.Gray, ReachedBrush = Brushes.Red
        };
        var window = new Window { Content = control, Width = 440, Height = 400 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var texts = control.GetVisualDescendants().OfType<LyricText>().ToArray();
            var word = Assert.Single(texts, text => text.Text == "Word lyric");
            var romanizedWord = Assert.Single(texts, text => text.Text == "word lyric");
            var line = Assert.Single(texts, text => text.Text == "Line lyric");
            var romanizedLine = Assert.Single(texts, text => text.Text == "line lyric");
            var last = Assert.Single(texts, text => text.Text == "Last lyric");
            foreach (var reduceMotion in new[] { false, true })
            {
                control.ReduceMotion = reduceMotion;
                control.IsStopped = false;
                control.PositionMilliseconds = 500;
                Assert.DoesNotContain(texts, text => text.IsHighlighted);
                var unreached = CountRedPixels(window, word, 500);
                control.PositionMilliseconds = 1400;
                var completed = CountRedPixels(window, word, 1500);
                Assert.True(completed > unreached);
                foreach (var position in new[] { 1500, 2200, 2999 })
                {
                    control.PositionMilliseconds = position;
                    Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                    Assert.True(word.IsHighlighted); Assert.True(romanizedWord.IsHighlighted);
                    Assert.False(line.IsHighlighted);
                    Assert.Equal(completed, CountRedPixels(window, word, position));
                }
                foreach (var position in new[] { 3000, 3400, 4999 })
                {
                    control.PositionMilliseconds = position;
                    Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                    Assert.False(word.IsHighlighted); Assert.False(romanizedWord.IsHighlighted);
                    Assert.Equal(unreached, CountRedPixels(window, word, position));
                    Assert.True(line.IsHighlighted); Assert.True(romanizedLine.IsHighlighted);
                    Assert.False(last.IsHighlighted);
                }
                control.PositionMilliseconds = 6000;
                Assert.False(line.IsHighlighted); Assert.True(last.IsHighlighted);
                control.PositionMilliseconds = 2200;
                Assert.True(word.IsHighlighted); Assert.True(romanizedWord.IsHighlighted);
                Assert.False(last.IsHighlighted);
                control.IsStopped = true;
                Assert.DoesNotContain(texts, text => text.IsHighlighted);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TranslationsRemainVisibleAfterEmptyEndCuesAndWhenSeekingBetweenRepeatedLines()
    {
        var control = new LyricsPresenter
        {
            Document = new Lyrics("Repeated\nRepeated\nUntranslated", Lines:
                [new("Repeated", 1000, 2004), new("Repeated", 3000, 4007), new("Untranslated", 5000, 6000)],
                TranslationLines: [new("第一句", 800), new("", 2000), new("第二句", 3200), new(" \t", 4000), new("", 5000)]),
            IsStopped = false, PositionMilliseconds = 1900, FontSize = 24
        };
        var window = new Window { Content = control, Width = 440, Height = 400 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var lines = Assert.Single(control.GetVisualDescendants().OfType<StackPanel>(), panel => panel.Name == "LyricsLines");
            var rows = lines.Children.OfType<StackPanel>().ToArray();
            var translations = rows.Select(row => Assert.Single(row.Children.OfType<TextBlock>(), text => text is not LyricText)).ToArray();

            void AssertTranslations()
            {
                Assert.Equal("第一句", translations[0].Text); Assert.True(translations[0].IsVisible);
                Assert.Equal("第二句", translations[1].Text); Assert.True(translations[1].IsVisible);
                Assert.False(translations[2].IsVisible);
            }

            AssertTranslations();
            // Let the playback clock cross the first empty cue, then pause in the main timeline's gap.
            control.IsPlaying = true;
            await PumpAsync(220);
            AssertTranslations();
            control.PositionMilliseconds = 2200;
            control.IsPlaying = false;
            AssertTranslations();
            foreach (var position in new[] { 2500, 3000, 4001, 4500, 5000, 1900, 4001, 6000 })
            {
                control.PositionMilliseconds = position;
                AssertTranslations();
            }
            control.IsStopped = true;
            AssertTranslations();
            control.ShowTranslation = false;
            Assert.All(translations, translation => Assert.False(translation.IsVisible));
            control.ShowTranslation = true;
            AssertTranslations();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ClickingSeeksOnlyTimedRowsWhenPlaybackAllowsItAndDraggingDoesNotSeek()
    {
        var positions = new List<double>();
        var control = CreatePresenter(); control.ReduceMotion = true;
        control.SeekCommand = new RelayCommand<double>(position => positions.Add(position));
        var window = new Window { Content = control, Width = 440, Height = 400 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var text = Assert.Single(control.GetVisualDescendants().OfType<LyricText>(), t => t.Text == "歌词 0");
            var point = text.TranslatePoint(new Point(text.Bounds.Width / 2, text.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Assert.Empty(positions);
            control.CanSeek = true;
            window.MouseDown(point, MouseButton.Left); window.MouseMove(point + new Vector(0, 40)); window.MouseUp(point + new Vector(0, 40), MouseButton.Left);
            Assert.Empty(positions);
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Assert.Equal(0, Assert.Single(positions));
            Assert.True(control.IsFollowing);
            control.Document = new Lyrics("Untimed lyrics");
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var untimed = Assert.Single(control.GetVisualDescendants().OfType<LyricText>(), t => t.Text == "Untimed lyrics");
            point = untimed.TranslatePoint(new Point(untimed.Bounds.Width / 2, untimed.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Assert.Single(positions);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DisplayOptionsUpdateAllRowsAndKeepTheCurrentLineCentered()
    {
        var control = CreatePresenter(); control.ReduceMotion = true;
        var window = new Window { Content = control, Width = 440, Height = 400 };
        window.Show();
        try
        {
            control.PositionMilliseconds = 5100;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var rowTexts = Assert.Single(control.GetVisualDescendants().OfType<StackPanel>(), panel => panel.Name == "LyricsLines")
                .Children.OfType<StackPanel>().SelectMany(row => row.Children.OfType<TextBlock>()).ToArray();
            Assert.All(rowTexts, text => Assert.Equal(FontWeight.Normal, text.FontWeight));
            control.BoldLyrics = true;
            control.FontSize = 36; control.TextAlignment = TextAlignment.Left;
            control.ShowRomanization = control.ShowTranslation = false;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var text = control.GetVisualDescendants().OfType<LyricText>().Where(t => t.IsEffectivelyVisible).ToArray();
            Assert.Equal(12, text.Length);
            Assert.All(rowTexts, text => Assert.Equal(FontWeight.Bold, text.FontWeight));
            Assert.All(text, t => { Assert.Equal(36, t.FontSize); Assert.Equal(TextAlignment.Left, t.TextAlignment); });
            Assert.DoesNotContain(control.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "翻译 5");
            var current = Assert.Single(text, t => t.IsHighlighted);
            var point = current.TranslatePoint(new Point(0, current.Bounds.Height / 2), control)!.Value;
            Assert.InRange(point.Y, 150, 210);
            control.ShowRomanization = control.ShowTranslation = true;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(24, control.GetVisualDescendants().OfType<LyricText>().Count(t => t.IsEffectivelyVisible));
            control.BoldLyrics = false;
            Assert.All(rowTexts, text => Assert.Equal(FontWeight.Normal, text.FontWeight));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LyricPreferencesPersistNormalizeAndPreserveConcurrentUpdates()
    {
        using var paths = new TestPaths();
        var store = new JsonAppConfigurationStore(paths);
        var appearance = new AppearanceService(Avalonia.Application.Current!, new LocalizationService(), store);
        // Existing installations only have the original appearance fields.
        await store.SetAsync(AppearanceService.ConfigurationKey, new { Theme = ThemePreference.Light, Language = LanguagePreference.English, ReduceMotion = true });
        await appearance.LoadAsync();
        Assert.Equal(24, appearance.Current.LyricFontSize);
        Assert.True(appearance.Current.ShowRomanization); Assert.True(appearance.Current.ShowTranslation);
        Assert.False(appearance.Current.BoldLyrics);
        await Task.WhenAll(appearance.UpdateAsync(p => p with { LyricFontSize = 32 }),
            appearance.UpdateAsync(p => p with { ShowRomanization = false }),
            appearance.UpdateAsync(p => p with { ShowTranslation = false, LyricAlignment = LyricAlignment.Left }),
            appearance.UpdateAsync(p => p with { BoldLyrics = true }));
        var restored = new AppearanceService(Avalonia.Application.Current!, new LocalizationService(), store);
        await restored.LoadAsync();
        Assert.Equal(32, restored.Current.LyricFontSize);
        Assert.False(restored.Current.ShowRomanization); Assert.False(restored.Current.ShowTranslation);
        Assert.True(restored.Current.BoldLyrics);
        Assert.Equal(LyricAlignment.Left, restored.Current.LyricAlignment);
        await restored.SetAsync(restored.Current with { LyricFontSize = double.PositiveInfinity, LyricAlignment = (LyricAlignment)99 });
        Assert.Equal(24, restored.Current.LyricFontSize); Assert.Equal(LyricAlignment.Center, restored.Current.LyricAlignment);
        await restored.SetAsync(restored.Current with { LyricFontSize = 200 });
        Assert.Equal(48, restored.Current.LyricFontSize);
        await appearance.SetAsync(new());
    }

    private static LyricsPresenter CreatePresenter() => new()
    {
        Document = new Lyrics("", Lines: Enumerable.Range(0, 12).Select(i => new LyricsLine($"歌词 {i}", i * 1000, (i + 1) * 1000,
            Translations: new Dictionary<string, string> { ["zh"] = $"翻译 {i}" }, Romanization: $"ge ci {i}",
            Words: [new($"歌词 {i}", i * 1000, (i + 1) * 1000)])).ToArray()),
        IsStopped = false, PositionMilliseconds = 100, FontSize = 24, Foreground = Brushes.Gray, ReachedBrush = Brushes.Red
    };

    private static int CountRedPixels(Window window, LyricText text, double position)
    {
        text.PositionMilliseconds = position;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height));
        bitmap.Render(text);
        using var copy = new WriteableBitmap(bitmap.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = copy.Lock(); bitmap.CopyPixels(buffer);
        var pixels = new byte[buffer.RowBytes * bitmap.PixelSize.Height]; Marshal.Copy(buffer.Address, pixels, 0, pixels.Length);
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 2] > pixels[i + 1] + 30 && pixels[i + 2] > pixels[i] + 30) count++;
        return count;
    }

    private static async Task PumpAsync(int milliseconds)
    {
        for (var elapsed = 0; elapsed < milliseconds; elapsed += 20)
        {
            await Task.Delay(20); Dispatcher.UIThread.RunJobs();
        }
    }
}
