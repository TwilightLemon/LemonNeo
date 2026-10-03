using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Desktop.Infrastructure;

/// <summary>Loads covers without blocking the UI. The caller owns the returned bitmap.</summary>
public sealed class ArtworkService(HttpClient httpClient, ILogger<ArtworkService> logger)
{
    private const int MaximumBytes = 16 * 1024 * 1024;
    private const int DecodeWidth = 512;

    public async Task<Bitmap?> LoadAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!uri.IsAbsoluteUri || !(uri.IsFile || uri.Scheme is "http" or "https")) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // Both file access and image decoding belong off the UI thread.
            return await Task.Run(() => LoadCoreAsync(uri, timeout.Token), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not load track artwork.");
            return null;
        }
    }

    private async Task<Bitmap> LoadCoreAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.IsFile)
        {
            await using var file = new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);
            return await DecodeAsync(file, cancellationToken).ConfigureAwait(false);
        }

        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new IOException("Artwork exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await DecodeAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Bitmap> DecodeAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var data = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (data.Length + read > MaximumBytes) throw new IOException("Artwork exceeds the size limit.");
            data.Write(buffer, 0, read);
        }
        cancellationToken.ThrowIfCancellationRequested();
        data.Position = 0;
        var bitmap = Bitmap.DecodeToWidth(data, DecodeWidth, BitmapInterpolationMode.HighQuality);
        if (cancellationToken.IsCancellationRequested)
        {
            bitmap.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return bitmap;
    }
}
