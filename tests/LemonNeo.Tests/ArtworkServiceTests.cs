using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LemonNeo.Desktop.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LemonNeo.Tests;

public sealed class ArtworkServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [AvaloniaFact]
    public async Task ConcurrentRowsAndLaterLoadsDecodeOnceAndOwnIndependentReferences()
    {
        await using var context = new ImageTestContext();
        var calls = 0;
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = CreateService(context, (_, width, _) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            Assert.True(release.Wait(Timeout));
            return Bitmap(width);
        });
        var requests = Enumerable.Range(0, 100).Select(_ => service.LoadAsync(ImageTestContext.Uri(), decodeWidth: 128)).ToArray();
        try
        {
            await started.Task.WaitAsync(Timeout);
        }
        finally
        {
            release.Set();
        }
        var images = await Task.WhenAll(requests).WaitAsync(Timeout);
        try
        {
            Assert.All(images, image => Assert.NotNull(image));
            var bitmap = images[0]!.Bitmap;
            Assert.All(images, image => Assert.Same(bitmap, image!.Bitmap));
            images[0]!.Dispose();
            images[0]!.Dispose();
            AssertUsable(images[1]!.Bitmap);
            using var again = await service.LoadAsync(ImageTestContext.Uri(), decodeWidth: 128);
            Assert.Same(bitmap, again!.Bitmap);
            Assert.Equal(1, calls);
            Assert.Equal(1, context.Requests);
        }
        finally
        {
            foreach (var image in images) image?.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task ActualDecoderUsesSeparateSizesAndSharesEncodedBuffer()
    {
        await using var context = new ImageTestContext();
        await using var service = new ArtworkService(context.Cache, NullLogger<ArtworkService>.Instance);
        using var small = await service.LoadAsync(ImageTestContext.Uri(), decodeWidth: 32);
        using var large = await service.LoadWithDataAsync(ImageTestContext.Uri(), decodeWidth: 128);
        Assert.Equal(32, small!.Bitmap.PixelSize.Width);
        Assert.Equal(128, large!.Bitmap.PixelSize.Width);
        Assert.NotSame(small.Bitmap, large.Bitmap);
        Assert.True(MemoryMarshal.TryGetArray(small.EncodedData, out var smallData));
        Assert.True(MemoryMarshal.TryGetArray(large.EncodedData, out var largeData));
        Assert.Same(smallData.Array, largeData.Array);
        AssertUsable(small.Bitmap);
    }

    [AvaloniaFact]
    public async Task EvictionAndServiceShutdownKeepActiveReferencesAlive()
    {
        await using var context = new ImageTestContext();
        var calls = 0;
        await using var service = CreateService(context, (_, width, _) =>
        {
            Interlocked.Increment(ref calls);
            return Bitmap(width);
        }, maximumMemoryBytes: context.Data.Length + 16 * 16 * 4);
        using var first = await service.LoadAsync(ImageTestContext.Uri("a"), decodeWidth: 16);
        using var second = await service.LoadAsync(ImageTestContext.Uri("b"), decodeWidth: 16);
        AssertUsable(first!.Bitmap);
        using var reloaded = await service.LoadAsync(ImageTestContext.Uri("a"), decodeWidth: 16);
        Assert.NotSame(first.Bitmap, reloaded!.Bitmap);
        Assert.Equal(3, calls);
        await service.DisposeAsync();
        AssertUsable(first.Bitmap);
        AssertUsable(second!.Bitmap);
        AssertUsable(reloaded.Bitmap);
        var firstBitmap = first.Bitmap;
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => AssertUsable(firstBitmap));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.LoadAsync(ImageTestContext.Uri()));
    }

    [AvaloniaFact]
    public async Task CancellingOneRowDoesNotCancelAnotherRowsDecode()
    {
        await using var context = new ImageTestContext();
        using var cancel = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var service = CreateService(context, (_, width, _) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            Assert.True(release.Wait(Timeout));
            return Bitmap(width);
        });
        var abandoned = service.LoadAsync(ImageTestContext.Uri(), cancel.Token);
        var remaining = service.LoadAsync(ImageTestContext.Uri());
        try
        {
            await started.Task.WaitAsync(Timeout);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        }
        finally
        {
            release.Set();
        }
        using var image = await remaining.WaitAsync(Timeout);
        Assert.NotNull(image);
        AssertUsable(image.Bitmap);
        Assert.Equal(1, calls);
    }

    [AvaloniaFact]
    public async Task CancelledQueuedWorkNeverDecodesAndNativeWorkKeepsItsSlot()
    {
        await using var context = new ImageTestContext();
        using var firstCancellation = new CancellationTokenSource();
        using var queuedCancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var service = CreateService(context, (_, width, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                Assert.True(release.Wait(Timeout));
            }
            return Bitmap(width);
        }, maximumConcurrentDecodes: 1);
        var first = service.LoadAsync(ImageTestContext.Uri("first"), firstCancellation.Token);
        try
        {
            await started.Task.WaitAsync(Timeout);
            var queued = service.LoadAsync(ImageTestContext.Uri("queued"), queuedCancellation.Token);
            var next = service.LoadAsync(ImageTestContext.Uri("next"));
            firstCancellation.Cancel();
            queuedCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            // Downloads can finish while the one native decode is still blocked.
            await context.Cache.GetAsync(ImageTestContext.Uri("next"));
            Assert.False(next.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref calls));
            release.Set();
            using var image = await next.WaitAsync(Timeout);
            Assert.NotNull(image);
            Assert.Equal(2, calls);
            // The cancelled running result must not have been cached.
            using var retry = await service.LoadAsync(ImageTestContext.Uri("first"));
            Assert.NotNull(retry);
            Assert.Equal(3, calls);
        }
        finally
        {
            release.Set();
        }
    }

    [AvaloniaFact]
    public async Task ShutdownWaitsForNativeDecodesAndCancelsTheirResults()
    {
        await using var context = new ImageTestContext();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = CreateService(context, (_, width, _) =>
        {
            started.TrySetResult();
            Assert.True(release.Wait(Timeout));
            return Bitmap(width);
        });
        var load = service.LoadAsync(ImageTestContext.Uri());
        try
        {
            await started.Task.WaitAsync(Timeout);
            var shutdown = service.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);
            Assert.Same(shutdown, service.DisposeAsync().AsTask());
            release.Set();
            await shutdown.WaitAsync(Timeout);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        }
        finally
        {
            release.Set();
        }
    }

    [AvaloniaFact]
    public async Task BadDiskDataIsInvalidatedAndRetriedFromSource()
    {
        await using var context = new ImageTestContext(maximumMemoryBytes: 0);
        await context.Cache.GetAsync(ImageTestContext.Uri());
        var file = Assert.Single(Directory.GetFiles(context.Cache.DirectoryPath, "*.image"));
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        await using var service = new ArtworkService(context.Cache, NullLogger<ArtworkService>.Instance);
        using var image = await service.LoadAsync(ImageTestContext.Uri());
        Assert.NotNull(image);
        Assert.Equal(2, context.Requests);
        AssertUsable(image.Bitmap);
    }

    private static ArtworkService CreateService(ImageTestContext context,
        Func<ReadOnlyMemory<byte>, int, CancellationToken, Bitmap> decode,
        long maximumMemoryBytes = 64 * 1024 * 1024, int maximumConcurrentDecodes = 2) =>
        new(context.Cache, NullLogger<ArtworkService>.Instance, maximumMemoryBytes, maximumConcurrentDecodes, decode);

    private static Bitmap Bitmap(int width) => new WriteableBitmap(new PixelSize(width, width),
        new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Premul);

    private static void AssertUsable(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        Assert.True(stream.Length > 0);
    }
}
