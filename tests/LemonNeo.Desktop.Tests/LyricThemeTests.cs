using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LemonNeo.Application;
using LemonNeo.Desktop.Controls;
using LemonNeo.Plugin.Abstractions;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class LyricThemeTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyLyricFragmentsPreserveTheFollowingHighlight(bool useWords)
    {
        string[] texts = ["", "FIRST ", "", "SECOND", ""];
        var lyric = new LyricText
        {
            FontSize = 40, Margin = new Thickness(12), Foreground = Brushes.White,
            ReachedBrush = Brushes.Lime, IsHighlighted = true, ReduceMotion = true,
            PositionMilliseconds = 2000
        };
        if (useWords)
            lyric.Words = texts.Select((text, index) => new LyricsWord(text, index * 1000, (index + 1) * 1000)).ToArray();
        else
            lyric.Segments = texts.Select((text, index) => new LyricsDisplaySegment(text, index <= 2)).ToArray();
        var window = new Window { Width = 400, Height = 100, Background = Brushes.Black, Content = lyric };
        try
        {
            window.Show();
            Assert.Equal("FIRST SECOND", lyric.Text);
            using var firstWord = window.CaptureRenderedFrame();
            Assert.NotNull(firstWord);
            var initialHighlight = CountColoredPixels(firstWord, magenta: false);
            Assert.True(initialHighlight > 100);

            if (useWords) lyric.PositionMilliseconds = 4000;
            else lyric.Segments = texts.Select(text => new LyricsDisplaySegment(text, true)).ToArray();
            using var bothWords = window.CaptureRenderedFrame();
            Assert.NotNull(bothWords);
            Assert.True(CountColoredPixels(bothWords, magenta: false) > initialHighlight + 100);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PausedLyricHighlightRedrawsWhenTheSharedThemeBrushChanges()
    {
        var highlight = new SolidColorBrush(Colors.Lime);
        var lyric = new LyricText
        {
            Text = "COLOR", FontSize = 40, Margin = new Thickness(12),
            Foreground = Brushes.White, ReachedBrush = highlight, IsHighlighted = true, ReduceMotion = true
        };
        var window = new Window { Width = 240, Height = 100, Background = Brushes.Black, Content = lyric };
        window.Show();
        try
        {
            using var initial = window.CaptureRenderedFrame();
            Assert.NotNull(initial);
            Assert.True(CountColoredPixels(initial, magenta: false) > 100);
            highlight.Color = Colors.Magenta;
            using var changed = window.CaptureRenderedFrame();
            Assert.NotNull(changed);
            Assert.True(CountColoredPixels(changed, magenta: true) > 100);
            Assert.Equal(0, CountColoredPixels(changed, magenta: false));
        }
        finally { window.Close(); }
    }

    private static int CountColoredPixels(Bitmap bitmap, bool magenta)
    {
        using var pixels = new WriteableBitmap(bitmap.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var framebuffer = pixels.Lock();
        bitmap.CopyPixels(framebuffer);
        var data = new byte[framebuffer.RowBytes * bitmap.PixelSize.Height];
        Marshal.Copy(framebuffer.Address, data, 0, data.Length);
        var count = 0;
        for (var i = 0; i < data.Length; i += 4)
            if (magenta ? data[i] > data[i + 1] + 20 && data[i + 2] > data[i + 1] + 20
                : data[i + 1] > data[i] + 20 && data[i + 1] > data[i + 2] + 20) count++;
        return count;
    }
}
