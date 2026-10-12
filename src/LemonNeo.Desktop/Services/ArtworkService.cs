using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using LemonNeo.Application;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Desktop.Services;

/// <summary>A caller-owned reference to shared artwork. Dispose the reference, never its Bitmap.</summary>
public sealed class LoadedArtwork : IDisposable
{
    private SharedArtwork? _artwork;

    internal LoadedArtwork(SharedArtwork artwork)
    {
        _artwork = artwork;
        artwork.AddReference();
    }

    public Bitmap Bitmap => GetArtwork().Bitmap;
    public ReadOnlyMemory<byte> EncodedData => GetArtwork().EncodedData;

    private SharedArtwork GetArtwork() => _artwork ?? throw new ObjectDisposedException(nameof(LoadedArtwork));

    public void Dispose() => Interlocked.Exchange(ref _artwork, null)?.Release();
}

internal sealed class SharedArtwork(Bitmap bitmap, ReadOnlyMemory<byte> encodedData)
{
    // The completed request owns the initial reference until all its waiters have left.
    private int _references = 1;
    public Bitmap Bitmap { get; } = bitmap;
    public ReadOnlyMemory<byte> EncodedData { get; } = encodedData;
    public long Size { get; } = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4 +
                               (MemoryMarshal.TryGetArray(encodedData, out var segment)
                                   ? segment.Array!.LongLength : encodedData.Length);
    public LinkedListNode<(string Uri, int Width)>? CacheNode { get; set; }

    public void AddReference() => Interlocked.Increment(ref _references);

    public void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0) Bitmap.Dispose();
    }
}

/// <summary>Shares bounded, decoded artwork by URI and size, with caller-owned references.</summary>
public sealed class ArtworkService : IAsyncDisposable
{
    private readonly ImageCache _cache;
    private readonly ILogger<ArtworkService> _logger;
    private readonly Func<ReadOnlyMemory<byte>, int, CancellationToken, Bitmap> _decode;
    private readonly SemaphoreSlim _decodeConcurrency;
    private readonly long _maximumMemoryBytes;
    private readonly object _gate = new();
    private readonly Dictionary<(string Uri, int Width), SharedArtwork> _images = [];
    private readonly LinkedList<(string Uri, int Width)> _recent = [];
    private readonly Dictionary<(string Uri, int Width), Request> _requests = [];
    // Also includes abandoned requests whose native decoder has not returned yet.
    private readonly HashSet<Request> _workers = [];
    private long _memoryBytes;
    private bool _closing;
    private Task? _disposeTask;

    public ArtworkService(ImageCache cache, ILogger<ArtworkService> logger,
        long maximumMemoryBytes = 64 * 1024 * 1024, int maximumConcurrentDecodes = 2)
        : this(cache, logger, maximumMemoryBytes, maximumConcurrentDecodes, Decode)
    {
    }

    internal ArtworkService(ImageCache cache, ILogger<ArtworkService> logger, long maximumMemoryBytes,
        int maximumConcurrentDecodes, Func<ReadOnlyMemory<byte>, int, CancellationToken, Bitmap> decode)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumMemoryBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConcurrentDecodes);
        _cache = cache;
        _logger = logger;
        _maximumMemoryBytes = maximumMemoryBytes;
        _decodeConcurrency = new SemaphoreSlim(maximumConcurrentDecodes);
        _decode = decode;
    }

    public Task<LoadedArtwork?> LoadWithDataAsync(Uri uri, CancellationToken cancellationToken = default,
        int decodeWidth = 512) => LoadAsync(uri, cancellationToken, decodeWidth);

    public async Task<LoadedArtwork?> LoadAsync(Uri uri, CancellationToken cancellationToken = default,
        int decodeWidth = 512)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(decodeWidth);
        if (!uri.IsAbsoluteUri || !(uri.IsFile || uri.Scheme is "http" or "https")) return null;
        var key = (uri.AbsoluteUri, decodeWidth);
        Request request;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_images.TryGetValue(key, out var image))
            {
                _recent.Remove(image.CacheNode!);
                _recent.AddLast(image.CacheNode!);
                return new LoadedArtwork(image);
            }

            if (!_requests.TryGetValue(key, out request!))
            {
                request = new Request();
                _requests.Add(key, request);
                _workers.Add(request);
                // Only one worker per URI/size, including when the encoded bytes are already cached.
                _ = Task.Run(() => CompleteRequestAsync(uri, key, request));
            }
            request.Waiters++;
        }

        try
        {
            var image = await request.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return image is null ? null : new LoadedArtwork(image);
        }
        finally
        {
            lock (_gate)
            {
                if (--request.Waiters == 0)
                {
                    if (_requests.GetValueOrDefault(key) == request) _requests.Remove(key);
                    if (request.Completed) request.Result?.Release();
                    else request.Cancellation.Cancel();
                }
            }
        }
    }

    private async Task CompleteRequestAsync(Uri uri, (string Uri, int Width) key, Request request)
    {
        SharedArtwork? image = null;
        try
        {
            image = await LoadCoreAsync(uri, key.Width, request.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not load track artwork.");
        }
        finally
        {
            lock (_gate)
            {
                var cancelled = request.Cancellation.IsCancellationRequested;
                if (cancelled)
                {
                    image?.Release();
                    image = null;
                }
                else if (image is not null) Remember(key, image);

                request.Result = image;
                request.Completed = true;
                if (cancelled) request.Completion.TrySetCanceled();
                else request.Completion.TrySetResult(image);
                if (request.Waiters == 0) image?.Release();
                request.Cancellation.Dispose();
                _workers.Remove(request);
                request.Done.SetResult();
            }
        }
    }

    private async Task<SharedArtwork?> LoadCoreAsync(Uri uri, int width, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var image = await _cache.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (image is null) return null;
            await _decodeConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bitmap = _decode(image.Data, width, cancellationToken);
                // Native decoding cannot be interrupted. Keep the slot occupied until it returns,
                // and never publish (or cache) an abandoned result.
                if (cancellationToken.IsCancellationRequested)
                {
                    bitmap.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return new SharedArtwork(bitmap, image.Data);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogDebug(exception, "Could not decode track artwork.");
                await _cache.InvalidateAsync(uri).ConfigureAwait(false);
                if (!image.IsCached) return null;
            }
            finally
            {
                _decodeConcurrency.Release();
            }
        }
        return null;
    }

    private void Remember((string Uri, int Width) key, SharedArtwork image)
    {
        if (_closing || image.Size > _maximumMemoryBytes) return;
        while (_memoryBytes + image.Size > _maximumMemoryBytes && _recent.First is { } oldest)
        {
            var previous = _images[oldest.Value];
            _images.Remove(oldest.Value);
            _recent.RemoveFirst();
            _memoryBytes -= previous.Size;
            previous.CacheNode = null;
            previous.Release();
        }
        image.AddReference();
        image.CacheNode = _recent.AddLast(key);
        _images.Add(key, image);
        _memoryBytes += image.Size;
    }

    private static Bitmap Decode(ReadOnlyMemory<byte> data, int width, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // ImageCache always returns array-backed memory; the stream borrows it without a full copy.
        if (!MemoryMarshal.TryGetArray(data, out var buffer))
            throw new InvalidDataException("Artwork must be backed by an encoded byte buffer.");
        using var stream = new MemoryStream(buffer.Array!, buffer.Offset, buffer.Count, false);
        return Bitmap.DecodeToWidth(stream, width);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Task[] workers;
        lock (_gate)
        {
            _closing = true;
            workers = _workers.Select(request => request.Done.Task).ToArray();
            foreach (var request in _workers) request.Cancellation.Cancel();
            foreach (var image in _images.Values) image.Release();
            _images.Clear();
            _recent.Clear();
            _memoryBytes = 0;
        }
        await Task.WhenAll(workers).ConfigureAwait(false);
        _decodeConcurrency.Dispose();
    }

    private sealed class Request
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<SharedArtwork?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Waiters { get; set; }
        public bool Completed { get; set; }
        public SharedArtwork? Result { get; set; }
    }
}
