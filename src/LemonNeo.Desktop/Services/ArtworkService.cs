using Avalonia.Media.Imaging;
using LemonNeo.Application;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Desktop.Services;

public sealed record LoadedArtwork(Bitmap Bitmap, ReadOnlyMemory<byte> EncodedData);

/// <summary>Decodes shared cached image bytes off the UI thread. The caller owns each returned bitmap.</summary>
public sealed class ArtworkService(ImageCache cache, ILogger<ArtworkService> logger)
{
    public async Task<Bitmap?> LoadAsync(Uri uri, CancellationToken cancellationToken = default, int decodeWidth = 512)
    {
        return (await LoadWithDataAsync(uri, cancellationToken, decodeWidth).ConfigureAwait(false))?.Bitmap;
    }

    public async Task<LoadedArtwork?> LoadWithDataAsync(Uri uri, CancellationToken cancellationToken = default,
        int decodeWidth = 512)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(decodeWidth);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var image = await cache.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (image is null) return null;
            try
            {
                var bitmap = await Task.Run(() => Decode(image.Data, decodeWidth, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
                return new LoadedArtwork(bitmap, image.Data);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
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
        using var stream = new MemoryStream(data.ToArray(), false);
        var bitmap = Bitmap.DecodeToWidth(stream, width);
        if (cancellationToken.IsCancellationRequested) bitmap.Dispose();
        return bitmap;
    }
}
