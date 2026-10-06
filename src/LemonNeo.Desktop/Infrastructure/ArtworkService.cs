using LemonNeo.Application;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Desktop.Infrastructure;

/// <summary>Decodes shared cached image bytes off the UI thread. The caller owns each returned bitmap.</summary>
public sealed class ArtworkService(ImageCache cache, ILogger<ArtworkService> logger)
{
    public async Task<Bitmap?> LoadAsync(Uri uri, CancellationToken cancellationToken = default, int decodeWidth = 512)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(decodeWidth);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var image = await cache.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (image is null) return null;
            try
            {
                return await Task.Run(() => Decode(image.Data, decodeWidth, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Could not decode track artwork.");
                await cache.InvalidateAsync(uri).ConfigureAwait(false);
                if (!image.IsCached) return null;
            }
        }
        return null;
    }

    private static Bitmap Decode(ReadOnlyMemory<byte> data, int width, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(data.ToArray(), writable: false);
        var bitmap = Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
        if (cancellationToken.IsCancellationRequested)
        {
            bitmap.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return bitmap;
    }
}
