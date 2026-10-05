using System.Net;
using Avalonia.Headless.XUnit;
using LemonNeo.Application;
using LemonNeo.Desktop.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class ImageCacheTests
{
    private static readonly Uri CoverUri = new("https://artwork.test/cover?size=512");

    [Fact]
    public async Task MemoryAndRestartedDiskHitsAvoidTheSourceAndUseTheUnifiedCacheDirectory()
    {
        using var paths = new TestPaths();
        using var handler = new ImageHandler([1, 2, 3, 4]);
        using var client = new HttpClient(handler);
        await using (var cache = Cache(paths, client))
        {
            Assert.Equal(Path.Combine(paths.CacheDirectory, "Images"), cache.DirectoryPath);
            var first = Assert.IsType<CachedImage>(await cache.GetAsync(CoverUri, TestContext.Current.CancellationToken));
            Assert.False(first.IsCached);
            var path = Assert.Single(Directory.GetFiles(cache.DirectoryPath, "*.image"));
            Assert.Equal(first.Data.ToArray(), await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            // A corrupt disk copy must not affect a memory hit.
            await File.WriteAllBytesAsync(path, [9], TestContext.Current.CancellationToken);
            Assert.Equal(first.Data.ToArray(), (await cache.GetAsync(CoverUri, TestContext.Current.CancellationToken))!.Data.ToArray());
            Assert.Equal(1, handler.Requests);
            await File.WriteAllBytesAsync(path, first.Data.ToArray(), TestContext.Current.CancellationToken);
        }
        handler.Status = HttpStatusCode.ServiceUnavailable;
        await using var restarted = Cache(paths, client);
        var disk = Assert.IsType<CachedImage>(await restarted.GetAsync(CoverUri, TestContext.Current.CancellationToken));
        Assert.True(disk.IsCached);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, disk.Data.ToArray());
        Assert.Equal(1, handler.Requests);
        Assert.Empty(Directory.GetFiles(restarted.DirectoryPath, "*.part"));
    }

    [Fact]
    public async Task CapacityOverflowFallsBackToDiskWithoutRedownloading()
    {
        using var paths = new TestPaths();
        using var handler = new ImageHandler([1, 2, 3, 4]);
        using var client = new HttpClient(handler);
        await using var cache = Cache(paths, client, memoryBytes: 8);
        var a = new Uri("https://artwork.test/a");
        var b = new Uri("https://artwork.test/b");
        var c = new Uri("https://artwork.test/c");
        await cache.GetAsync(a, TestContext.Current.CancellationToken);
        await cache.GetAsync(b, TestContext.Current.CancellationToken);
        await cache.GetAsync(c, TestContext.Current.CancellationToken);
        handler.Status = HttpStatusCode.ServiceUnavailable;
        foreach (var uri in new[] { a, b, c })
        {
            var image = Assert.IsType<CachedImage>(await cache.GetAsync(uri, TestContext.Current.CancellationToken));
            Assert.True(image.IsCached);
            Assert.Equal(handler.Bytes, image.Data.ToArray());
        }
        Assert.Equal(3, handler.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task MemoryAdmissionUsesImageByteLengthAndSupportsDisablingMemory(long memoryBytes)
    {
        using var paths = new TestPaths();
        using var handler = new ImageHandler([1, 2, 3, 4]);
        using var client = new HttpClient(handler);
        await using var cache = Cache(paths, client, memoryBytes);
        Assert.NotNull(await cache.GetAsync(CoverUri, TestContext.Current.CancellationToken));
        File.Delete(Assert.Single(Directory.GetFiles(cache.DirectoryPath, "*.image")));
        handler.Status = HttpStatusCode.ServiceUnavailable;
        var result = await cache.GetAsync(CoverUri, TestContext.Current.CancellationToken);
        if (memoryBytes >= handler.Bytes.Length)
        {
            Assert.NotNull(result);
            Assert.True(result.IsCached);
            Assert.Equal(handler.Bytes, result.Data.ToArray());
            Assert.Equal(1, handler.Requests);
        }
        else
        {
            Assert.Null(result);
            Assert.Equal(2, handler.Requests);
        }
    }

    [Fact]
    public async Task ConcurrentRequestsShareOneDownloadAndCancellationOnlyStopsItsCaller()
    {
        using var paths = new TestPaths();
        using var handler = new ImageHandler([1, 2, 3]) { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var client = new HttpClient(handler);
        await using var cache = Cache(paths, client);
        using var cancelled = new CancellationTokenSource();
        var first = cache.GetAsync(CoverUri, cancelled.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var others = Enumerable.Range(0, 12).Select(_ => cache.GetAsync(CoverUri, TestContext.Current.CancellationToken)).ToArray();
        try
        {
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.Equal(1, handler.Requests);
            handler.Release.TrySetResult();
            var results = await Task.WhenAll(others).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.All(results, image => Assert.Equal(new byte[] { 1, 2, 3 }, image!.Data.ToArray()));
            Assert.Equal(1, handler.Requests);
        }
        finally { handler.Release.TrySetResult(); }
    }

    [Fact]
    public async Task ShutdownCancelsPendingDownloadsAndLeavesNoPartialFiles()
    {
        using var paths = new TestPaths();
        using var handler = new ImageHandler([1]) { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var client = new HttpClient(handler);
        var cache = Cache(paths, client);
        var pending = Enumerable.Range(0, 8).Select(index => cache.GetAsync(new Uri($"https://artwork.test/{index}"), TestContext.Current.CancellationToken)).ToArray();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await cache.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        foreach (var request in pending) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.False(Directory.Exists(cache.DirectoryPath));
    }

    [Fact]
    public async Task DifferentUrlQueriesHaveSeparateEntriesAndUnsupportedUrisAreIgnored()
    {
        using var paths = new TestPaths();
        using var handler = new ImageHandler([1]);
        using var client = new HttpClient(handler);
        await using var cache = Cache(paths, client);
        await cache.GetAsync(CoverUri, TestContext.Current.CancellationToken);
        await cache.GetAsync(new Uri("https://artwork.test/cover?size=128"), TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Requests);
        Assert.Equal(2, Directory.GetFiles(cache.DirectoryPath, "*.image").Length);
        Assert.Null(await cache.GetAsync(new Uri("relative.jpg", UriKind.Relative), TestContext.Current.CancellationToken));
        Assert.Null(await cache.GetAsync(new Uri("ftp://artwork.test/image"), TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task FailedEmptyAndOversizedDownloadsAreNotCachedAndCanBeRetried()
    {
        using var paths = new TestPaths();
        using var handler = new ImageHandler([1]) { Status = HttpStatusCode.NotFound };
        using var client = new HttpClient(handler);
        await using var cache = Cache(paths, client);
        Assert.Null(await cache.GetAsync(CoverUri, TestContext.Current.CancellationToken));
        handler.Status = HttpStatusCode.OK;
        handler.Bytes = [];
        Assert.Null(await cache.GetAsync(CoverUri, TestContext.Current.CancellationToken));
        handler.Bytes = [1];
        handler.ContentLength = ImageCache.MaximumImageBytes + 1;
        Assert.Null(await cache.GetAsync(CoverUri, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(cache.DirectoryPath));
        handler.ContentLength = null;
        Assert.NotNull(await cache.GetAsync(CoverUri, TestContext.Current.CancellationToken));
        Assert.Equal(4, handler.Requests);
    }

    [Fact]
    public async Task DiskWriteFailureStillReturnsTheImageAndKeepsItInMemory()
    {
        using var paths = new TestPaths();
        Directory.CreateDirectory(paths.CacheDirectory);
        await File.WriteAllTextAsync(Path.Combine(paths.CacheDirectory, "Images"), "occupied", TestContext.Current.CancellationToken);
        using var handler = new ImageHandler([1, 2]);
        using var client = new HttpClient(handler);
        await using var cache = Cache(paths, client);
        Assert.NotNull(await cache.GetAsync(CoverUri, TestContext.Current.CancellationToken));
        Assert.True((await cache.GetAsync(CoverUri, TestContext.Current.CancellationToken))!.IsCached);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task LocalFileCoversCanBeLoadedFromDiskAfterTheSourceIsRemoved()
    {
        using var paths = new TestPaths();
        Directory.CreateDirectory(paths.DataDirectory);
        var source = Path.Combine(paths.DataDirectory, "cover.png");
        await File.WriteAllBytesAsync(source, [1, 2, 3], TestContext.Current.CancellationToken);
        var uri = new Uri(source);
        using var handler = new ImageHandler([9]);
        using var client = new HttpClient(handler);
        await using (var cache = Cache(paths, client)) Assert.NotNull(await cache.GetAsync(uri, TestContext.Current.CancellationToken));
        File.Delete(source);
        await using var restarted = Cache(paths, client);
        Assert.Equal(new byte[] { 1, 2, 3 }, (await restarted.GetAsync(uri, TestContext.Current.CancellationToken))!.Data.ToArray());
        Assert.Equal(0, handler.Requests);
    }

    [AvaloniaFact]
    public async Task CorruptedDiskImagesAreRefetchedAndBitmapsHaveIndependentOwnership()
    {
        using var paths = new TestPaths();
        using var handler = new ImageHandler(CreatePng(SKColors.Teal));
        using var client = new HttpClient(handler);
        string path;
        await using (var cache = Cache(paths, client))
        {
            using var loaded = await Artwork(cache).LoadAsync(CoverUri);
            Assert.NotNull(loaded);
            path = Assert.Single(Directory.GetFiles(cache.DirectoryPath, "*.image"));
        }
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await using var restarted = Cache(paths, client);
        var artwork = Artwork(restarted);
        using var repaired = await artwork.LoadAsync(CoverUri);
        Assert.NotNull(repaired);
        using var thumbnail = await artwork.LoadAsync(CoverUri, decodeWidth: 128);
        Assert.NotNull(thumbnail);
        Assert.NotSame(repaired, thumbnail);
        repaired.Dispose();
        Assert.Equal(128, thumbnail.PixelSize.Width);
        using var output = new MemoryStream();
        thumbnail.Save(output, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        Assert.True(output.Length > 0);
        Assert.Equal(2, handler.Requests);
        Assert.Equal(handler.Bytes, await File.ReadAllBytesAsync(path));
    }

    [AvaloniaFact]
    public async Task InvalidSourceImagesLeaveNoCacheEntryAndDisplayCanRetryLater()
    {
        using var paths = new TestPaths();
        using var handler = new ImageHandler([1, 2, 3]);
        using var client = new HttpClient(handler);
        await using var cache = Cache(paths, client);
        var artwork = Artwork(cache);
        Assert.Null(await artwork.LoadAsync(CoverUri));
        Assert.Empty(Directory.GetFiles(cache.DirectoryPath));
        handler.Bytes = CreatePng(SKColors.Red);
        using var loaded = await artwork.LoadAsync(CoverUri);
        Assert.NotNull(loaded);
        Assert.Equal(2, handler.Requests);
    }

    internal static byte[] CreatePng(SKColor color)
    {
        using var bitmap = new SKBitmap(20, 20);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal static ImageCache Cache(TestPaths paths, HttpClient client, long memoryBytes = 64 * 1024 * 1024) =>
        new(paths.CacheDirectory, client, NullLogger<ImageCache>.Instance, memoryBytes);
    internal static ArtworkService Artwork(ImageCache cache) => new(cache, NullLogger<ArtworkService>.Instance);

    internal sealed class ImageHandler(byte[] bytes) : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public byte[] Bytes { get; set; } = bytes;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public long? ContentLength { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            Started.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(cancellationToken);
            var content = new ByteArrayContent(Bytes);
            if (ContentLength is { } length) content.Headers.ContentLength = length;
            return new(Status) { Content = content };
        }
    }
}
