using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LemonNeo.Desktop.Infrastructure;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class ArtworkColorTests
{
    [Fact]
    public void WhiteBorderAndSmallBrightDetailsDoNotHideTheArtworkColor()
    {
        var pixels = Enumerable.Repeat(Colors.White, 600).Concat(Enumerable.Repeat(Color.Parse("#367EAD"), 399)).Append(Colors.Red).ToArray();
        Assert.Equal(Color.Parse("#367EAD"), Extract(pixels));
    }

    [Fact]
    public void TransparentPixelsAreIgnoredAndEmptyArtworkHasNoColor()
    {
        Assert.Null(Extract(Enumerable.Repeat(Colors.Transparent, 100).ToArray()));
        Assert.Equal(Colors.Green, Extract([Colors.Transparent, Colors.Green, Colors.Transparent]));
    }

    [Theory]
    [InlineData("#FFFFFF", "#808080")]
    [InlineData("#000000", "#808080")]
    [InlineData("#808080", "#808080")]
    public void MonochromeArtworkProducesANeutralPalette(string hex, string expected)
    {
        var color = Color.Parse(hex);
        var extracted = Extract(Enumerable.Repeat(color, 100).ToArray());
        Assert.Equal(Color.Parse(expected), extracted);
        Assert.Equal(0, ThemePalette.FromArtwork(extracted!.Value).Accent.ToHsl().S);
    }

    [Fact]
    public void SaturatedAccentCanOutweighALargerMutedRegion()
    {
        var accent = Color.Parse("#3887CC");
        var pixels = Enumerable.Repeat(Color.Parse("#859AA6"), 700).Concat(Enumerable.Repeat(accent, 300)).ToArray();
        Assert.Equal(accent, Extract(pixels));
    }

    [Fact]
    public void BalancedLightnessCanOutweighALargerDarkRegion()
    {
        var accent = Color.Parse("#E05050");
        var pixels = Enumerable.Repeat(Color.Parse("#4E0808"), 600).Concat(Enumerable.Repeat(accent, 400)).ToArray();
        Assert.Equal(accent, Extract(pixels));
    }

    [Fact]
    public void NearbyDistinctAccentsAreNotMergedIntoAnInventedColor()
    {
        var dominant = Color.Parse("#B05080");
        var pixels = Enumerable.Repeat(Color.Parse("#A05080"), 400).Concat(Enumerable.Repeat(dominant, 600)).ToArray();
        Assert.Equal(dominant, Extract(pixels));
    }

    [Fact]
    public void SimilarColorsInOneBinUseTheirAverage()
    {
        Assert.Equal(Color.FromRgb(167, 87, 135), Extract([Color.FromRgb(160, 80, 128), Color.FromRgb(174, 94, 142)]));
    }

    [Fact]
    public void GrayscaleArtworkUsesTheLargestRegionWhenSaturationScoresTie()
    {
        var dominant = Color.Parse("#909090");
        var pixels = Enumerable.Repeat(Color.Parse("#404040"), 10).Concat(Enumerable.Repeat(dominant, 90)).ToArray();
        Assert.Equal(dominant, Extract(pixels));
    }

    [AvaloniaFact]
    public void FineArtworkDetailsKeepTheirOriginalHueDuringSampling()
    {
        using var bitmap = new WriteableBitmap(new PixelSize(128, 128), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var framebuffer = bitmap.Lock())
        {
            var data = new byte[framebuffer.RowBytes * 128];
            for (var y = 0; y < 128; y++)
            for (var x = 0; x < 128; x++)
            {
                var offset = y * framebuffer.RowBytes + x * 4;
                var color = (x + y) % 3 == 0 ? Colors.Red : Colors.Blue;
                data[offset] = color.B; data[offset + 1] = color.G; data[offset + 2] = color.R; data[offset + 3] = 255;
            }
            Marshal.Copy(data, 0, framebuffer.Address, data.Length);
        }
        using var encoded = new MemoryStream();
        bitmap.Save(encoded, PngBitmapEncoderOptions.Default);
        encoded.Position = 0;
        using var decoded = new Bitmap(encoded);
        Assert.Equal(Colors.Blue, ArtworkColorExtractor.Extract(decoded));
    }

    [AvaloniaFact]
    public void BitmapSamplingHandlesPixelFormatAndAlpha()
    {
        using var bitmap = new WriteableBitmap(new PixelSize(100, 80), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Premul);
        using (var framebuffer = bitmap.Lock())
        {
            var data = new byte[framebuffer.RowBytes * 80];
            for (var y = 0; y < 80; y++)
            for (var x = 0; x < 100; x++)
            {
                var offset = y * framebuffer.RowBytes + x * 4;
                data[offset] = 24; data[offset + 1] = 96; data[offset + 2] = 48; data[offset + 3] = 192;
            }
            Marshal.Copy(data, 0, framebuffer.Address, data.Length);
        }
        // Playback receives decoded images, rather than render-target/writeable bitmap backends.
        using var encoded = new MemoryStream();
        bitmap.Save(encoded, PngBitmapEncoderOptions.Default);
        encoded.Position = 0;
        using var decoded = new Bitmap(encoded);
        var extracted = ArtworkColorExtractor.Extract(decoded);
        Assert.NotNull(extracted);
        Assert.InRange(extracted.Value.R, 30, 34);
        Assert.InRange(extracted.Value.G, 126, 130);
        Assert.InRange(extracted.Value.B, 62, 66);
    }

    [Fact]
    public void CancelledExtractionStopsBeforeReturningAColor()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ArtworkColorExtractor.ExtractPixels(new byte[4], 1, 1, 4, cancellation.Token));
    }

    private static Color? Extract(Color[] pixels)
    {
        var data = pixels.SelectMany(c => new[] { c.B, c.G, c.R, c.A }).ToArray();
        return ArtworkColorExtractor.ExtractPixels(data, pixels.Length, 1, pixels.Length * 4);
    }
}
