using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace LemonNeo.Desktop.Infrastructure;

/// <summary>Small, alpha-aware color histogram; decoding and extraction run away from the UI thread.</summary>
public static class ArtworkColorExtractor
{
    public static Color? Extract(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scale = Math.Min(1, 64d / Math.Max(bitmap.PixelSize.Width, bitmap.PixelSize.Height));
        var size = new PixelSize(Math.Max(1, (int)(bitmap.PixelSize.Width * scale)), Math.Max(1, (int)(bitmap.PixelSize.Height * scale)));
        using var sampled = bitmap.CreateScaledBitmap(size, BitmapInterpolationMode.HighQuality);
        using var pixels = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var framebuffer = pixels.Lock();
        sampled.CopyPixels(framebuffer);
        var data = new byte[framebuffer.RowBytes * size.Height];
        Marshal.Copy(framebuffer.Address, data, 0, data.Length);
        return ExtractPixels(data, size.Width, size.Height, framebuffer.RowBytes, cancellationToken);
    }

    public static Color? ExtractPixels(byte[] bgra, int width, int height, int stride, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || stride < (long)width * 4 || (long)stride * height > bgra.Length)
            throw new ArgumentException("Invalid BGRA pixel buffer.", nameof(bgra));
        var bins = new Dictionary<int, ColorBin>();
        double totalWeight = 0;
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                var alpha = bgra[offset + 3];
                if (alpha < 128) continue;
                var r = bgra[offset + 2]; var g = bgra[offset + 1]; var b = bgra[offset];
                var key = (r >> 5) << 6 | (g >> 5) << 3 | b >> 5;
                if (!bins.TryGetValue(key, out var bin)) bins[key] = bin = new();
                var weight = alpha / 255d;
                bin.Weight += weight; bin.R += r * weight; bin.G += g * weight; bin.B += b * weight;
                totalWeight += weight;
            }
        }
        if (bins.Count == 0) return null;
        // Ignore tiny bright details. Prefer a substantial chromatic region over white borders.
        var candidates = bins.Values.Where(b => b.Weight >= totalWeight * .015).ToArray();
        if (candidates.Length == 0) candidates = bins.Values.ToArray();
        var chromatic = candidates.Where(b => { var hsl = b.Color.ToHsl(); return hsl.S >= .12 && hsl.L is > .10 and < .90; }).ToArray();
        var pool = chromatic.Length > 0 ? chromatic : candidates;
        return pool.MaxBy(b => b.Weight * (1 + b.Color.ToHsl().S * .5))!.Color;
    }

    private sealed class ColorBin
    {
        public double Weight, R, G, B;
        public Color Color => Color.FromRgb((byte)Math.Round(R / Weight), (byte)Math.Round(G / Weight), (byte)Math.Round(B / Weight));
    }
}
