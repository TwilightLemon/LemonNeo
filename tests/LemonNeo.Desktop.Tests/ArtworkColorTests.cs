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
    [InlineData("#FFFFFF")]
    [InlineData("#000000")]
    [InlineData("#808080")]
    public void MonochromeArtworkProducesANeutralPalette(string hex)
    {
        var color = Color.Parse(hex);
        var extracted = Extract(Enumerable.Repeat(color, 100).ToArray());
        Assert.Equal(color, extracted);
        Assert.Equal(0, ThemePalette.FromArtwork(extracted!.Value).Accent.ToHsl().S);
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
