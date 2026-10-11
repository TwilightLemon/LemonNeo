using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace LemonNeo.Desktop.Utils;

/// <summary>Adapts LemonLite's GetMajorColor to an alpha-aware Avalonia pixel buffer.</summary>
public static class ArtworkColorExtractor
{
    public static Color? Extract(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Sample the decoded pixels directly: resizing again can blend distinct artwork colors.
        var size = bitmap.PixelSize;
        using var pixels = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var framebuffer = pixels.Lock();
        bitmap.CopyPixels(framebuffer);
        var data = new byte[framebuffer.RowBytes * size.Height];
        Marshal.Copy(framebuffer.Address, data, 0, data.Length);
        return ExtractPixels(data, size.Width, size.Height, framebuffer.RowBytes, cancellationToken);
    }

    public static Color? ExtractPixels(byte[] bgra, int width, int height, int stride,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || stride < (long)width * 4 || (long)stride * height > bgra.Length)
            throw new ArgumentException("Invalid BGRA pixel buffer.", nameof(bgra));
        var samplingRate = Math.Max(1, Math.Min(width, height) / 100);
        var bins = new Dictionary<int, ColorBin>();
        var hasVisibleSamples = false;
        for (var y = 0; y < height; y += samplingRate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x += samplingRate)
            {
                var offset = y * stride + x * 4;
                var alpha = bgra[offset + 3];
                if (alpha < 128) continue;
                hasVisibleSamples = true;
                var r = bgra[offset + 2];
                var g = bgra[offset + 1];
                var b = bgra[offset];
                var average = (r + g + b) / 3;
                if (average is < 24 or > 230) continue;
                // Sixteen-value RGB bins preserve accents that the previous coarser bins merged.
                var key = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                if (!bins.TryGetValue(key, out var bin)) bins[key] = bin = new ColorBin();
                var weight = alpha / 255d;
                bin.Weight += weight;
                bin.R += r * weight;
                bin.G += g * weight;
                bin.B += b * weight;
            }
        }

        if (bins.Count == 0) return hasVisibleSamples ? Colors.Gray : null;
        // As in GetMajorColor, favor saturation and lightness near 0.6 as well as population.
        // Population breaks zero-score ties so grayscale art follows its largest region.
        return bins.Values.MaxBy(b => (b.Score, b.Weight))!.Color;
    }

    private sealed class ColorBin
    {
        public double Weight, R, G, B;
        public Color Color => Color.FromRgb((byte)(R / Weight), (byte)(G / Weight), (byte)(B / Weight));

        public double Score
        {
            get
            {
                // Keep the mean channels unrounded while scoring, as GetMajorColor does.
                var r = R / Weight / 255;
                var g = G / Weight / 255;
                var b = B / Weight / 255;
                var maximum = Math.Max(r, Math.Max(g, b));
                var minimum = Math.Min(r, Math.Min(g, b));
                var lightness = (maximum + minimum) / 2;
                var saturation = maximum == minimum
                    ? 0
                    : (maximum - minimum) / (lightness > .5 ? 2 - maximum - minimum : maximum + minimum);
                return Weight * saturation * (1 - Math.Abs(lightness - .6) * 1.8);
            }
        }
    }
}