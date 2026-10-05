using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Application;

public sealed record CachedImage(ReadOnlyMemory<byte> Data, bool IsCached);

/// <summary>Shared image bytes, read from bounded memory, host-owned disk storage, then the source URI.</summary>
public sealed class ImageCache : IAsyncDisposable
{
    public const int MaximumImageBytes = 16 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ImageCache> _logger;
    private readonly long _maximumMemoryBytes;
    private readonly object _gate = new();
    private readonly MemoryCache _memory;
    private readonly Dictionary<string, Task<CachedImage?>> _requests = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _concurrency = new(4);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _closing;

    public ImageCache(string cacheDirectory, HttpClient httpClient, ILogger<ImageCache> logger,
        long maximumMemoryBytes = 64 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumMemoryBytes);
        DirectoryPath = Path.Combine(Path.GetFullPath(cacheDirectory), "Images");
        _httpClient = httpClient;
        _logger = logger;
        _maximumMemoryBytes = maximumMemoryBytes;
        _memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = maximumMemoryBytes });
    }

    public string DirectoryPath { get; }

    public async Task<CachedImage?> GetAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!uri.IsAbsoluteUri || !(uri.IsFile || uri.Scheme is "http" or "https")) return null;
        var key = GetKey(uri);
        Task<CachedImage?> request;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_memory.TryGetValue<byte[]>(key, out var data) && data is not null)
                return new CachedImage(data, IsCached: true);
            if (!_requests.TryGetValue(key, out request!))
            {
                var completion = new TaskCompletionSource<CachedImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
                request = completion.Task;
                _requests.Add(key, request);
                // A caller cancelling its wait must not cancel another row's shared download.
                _ = Task.Run(() => CompleteRequestAsync(uri, key, completion));
            }
        }
        return await request.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteRequestAsync(Uri uri, string key, TaskCompletionSource<CachedImage?> completion)
    {
        CachedImage? result = null;
        var cancelled = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            await _concurrency.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var data = await ReadDiskAsync(key, timeout.Token).ConfigureAwait(false);
                var isCached = data is not null;
                data ??= await ReadSourceAsync(uri, timeout.Token).ConfigureAwait(false);
                if (!isCached) await StoreDiskAsync(key, data, timeout.Token).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                lock (_gate) Remember(key, data);
                result = new CachedImage(data, isCached);
            }
            finally { _concurrency.Release(); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not load an image from cache or its source.");
        }
        finally
        {
            lock (_gate)
            {
                _requests.Remove(key);
                if (cancelled) completion.TrySetCanceled(_lifetime.Token);
                else completion.TrySetResult(result);
            }
        }
    }

    private async Task<byte[]?> ReadDiskAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await using var file = new FileStream(GetPath(key), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await ReadBytesAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(exception, "Could not read a cached image; fetching it from its source.");
            return null;
        }
    }

    private async Task<byte[]> ReadSourceAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.IsFile)
        {
            await using var file = new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await ReadBytesAsync(file, cancellationToken).ConfigureAwait(false);
        }
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumImageBytes) throw new IOException("Image exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ReadBytesAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream.CanSeek && stream.Length > MaximumImageBytes) throw new IOException("Image exceeds the size limit.");
        using var data = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (data.Length + read > MaximumImageBytes) throw new IOException("Image exceeds the size limit.");
            data.Write(buffer, 0, read);
        }
        if (data.Length == 0) throw new IOException("Image is empty.");
        cancellationToken.ThrowIfCancellationRequested();
        return data.ToArray();
    }

    private async Task StoreDiskAsync(string key, byte[] data, CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            await File.WriteAllBytesAsync(temporaryPath, data, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, GetPath(key), overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(exception, "Could not store an image on disk; the loaded image remains available.");
        }
        finally { RemoveFile(temporaryPath); }
    }

    private void Remember(string key, byte[] data)
    {
        if (_closing || data.Length > _maximumMemoryBytes) return;
        _memory.Set(key, data, new MemoryCacheEntryOptions().SetSize(data.LongLength));
    }

    /// <summary>Evicts bytes that the image decoder rejected, allowing a fresh source request.</summary>
    public Task InvalidateAsync(Uri uri)
    {
        var key = GetKey(uri);
        lock (_gate)
        {
            if (!_closing) _memory.Remove(key);
        }
        return Task.Run(() => RemoveFile(GetPath(key)));
    }

    private void RemoveFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(exception, "Could not remove an image cache file.");
        }
    }

    private static string GetKey(Uri uri) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
    private string GetPath(string key) => Path.Combine(DirectoryPath, key + ".image");

    public async ValueTask DisposeAsync()
    {
        Task[] requests;
        lock (_gate)
        {
            if (_closing) return;
            _closing = true;
            requests = _requests.Values.ToArray();
            _memory.Clear();
        }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(requests).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _memory.Dispose();
        _concurrency.Dispose();
        _lifetime.Dispose();
    }
}
