using System.Runtime.InteropServices;
using LemonNeo.Application;
using Xunit;

namespace LemonNeo.Tests;

public sealed class ImageCacheTests
{
    [Fact]
    public async Task LocalArtworkIsReadWithoutCreatingAnotherDiskCopy()
    {
        await using var context = new ImageTestContext();
        var directory = Path.Combine(context.DirectoryPath, "Artwork");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "cover.png");
        await File.WriteAllBytesAsync(path, context.Data, TestContext.Current.CancellationToken);
        var uri = new Uri(path);
        var first = await context.Cache.GetAsync(uri, TestContext.Current.CancellationToken);
        var second = await context.Cache.GetAsync(uri, TestContext.Current.CancellationToken);
        Assert.Equal(context.Data, first!.Data.ToArray());
        Assert.True(second!.IsCached);
        Assert.True(MemoryMarshal.TryGetArray(first.Data, out var firstBuffer));
        Assert.True(MemoryMarshal.TryGetArray(second.Data, out var secondBuffer));
        Assert.Same(firstBuffer.Array, secondBuffer.Array);
        Assert.Equal(context.Data.Length, firstBuffer.Array!.Length);
        Assert.False(Directory.Exists(context.Cache.DirectoryPath));
        await context.Cache.InvalidateAsync(uri);
        Assert.True(File.Exists(path));
        Assert.Equal(0, context.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteBytesAreCorrectForKnownAndUnknownLengthsAndReuseDisk(bool chunked)
    {
        var bytes = new byte[300_001];
        new Random(42).NextBytes(bytes);
        await using var context = new ImageTestContext(bytes, chunked, maximumMemoryBytes: 0);
        var first = await context.Cache.GetAsync(ImageTestContext.Uri(), TestContext.Current.CancellationToken);
        var second = await context.Cache.GetAsync(ImageTestContext.Uri(), TestContext.Current.CancellationToken);
        Assert.Equal(bytes, first!.Data.ToArray());
        Assert.Equal(bytes, second!.Data.ToArray());
        Assert.False(first.IsCached);
        Assert.True(second.IsCached);
        Assert.Equal(1, context.Requests);
        var file = Assert.Single(Directory.GetFiles(context.Cache.DirectoryPath, "*.image"));
        Assert.Equal(bytes.Length, new FileInfo(file).Length);
        Assert.Empty(Directory.GetFiles(context.Cache.DirectoryPath, "*.part"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedImagesAreRejectedBeforeTheyEnterCaches(bool chunked)
    {
        await using var context = new ImageTestContext(new byte[ImageCache.MaximumImageBytes + 1], chunked);
        Assert.Null(await context.Cache.GetAsync(ImageTestContext.Uri(), TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(context.Cache.DirectoryPath));
    }
}
