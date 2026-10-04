using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LemonNeo.Desktop.Controls;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class LyricThemeTests
{
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
