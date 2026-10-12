using System.Net;
using LemonNeo.Application;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace LemonNeo.Tests;

internal sealed class ImageTestContext : IAsyncDisposable
{
    private readonly HttpClient _client;
    private int _requests;
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "LemonNeo-image-tests", Guid.NewGuid().ToString("N"));
    public byte[] Data { get; }
    public ImageCache Cache { get; }
    public int Requests => Volatile.Read(ref _requests);

    public ImageTestContext(byte[]? data = null, bool chunked = false, long maximumMemoryBytes = 64 * 1024 * 1024)
    {
        Data = data ?? CreatePng();
        _client = new HttpClient(new Handler(() =>
        {
            Interlocked.Increment(ref _requests);
            HttpContent content = chunked
                ? new StreamContent(new NonSeekableStream(Data)) : new ByteArrayContent(Data);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        Cache = new ImageCache(() => DirectoryPath, _client, NullLogger<ImageCache>.Instance, maximumMemoryBytes);
    }

    public static Uri Uri(string name = "cover") => new($"https://artwork.test/{name}.png");

    private static byte[] CreatePng()
    {
        using var bitmap = new SKBitmap(256, 128);
        bitmap.Erase(SKColors.LimeGreen);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await Cache.DisposeAsync();
        _client.Dispose();
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
    }

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response());
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes, false)
    {
        public override bool CanSeek => false;
    }
}
