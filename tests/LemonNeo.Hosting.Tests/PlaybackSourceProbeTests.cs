using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using LemonNeo.Abstractions.Playback;
using LemonNeo.Core.Playback;
using Xunit;

namespace LemonNeo.Hosting.Tests;

public sealed class PlaybackSourceProbeTests
{
    [Fact]
    public async Task PublicProbeForwardsSourceHeadersAndBoundsReadsWhenRangeIsIgnored()
    {
        var stream = new PartialReadStream(FlacHeader());
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("bytes=0-41", request.Headers.Range?.ToString());
            Assert.Equal("https://example.com/", request.Headers.Referrer?.AbsoluteUri);
            Assert.Equal("test-token", Assert.Single(request.Headers.GetValues("X-Audio-Token")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        }));
        var source = new HttpPlaybackSource
        {
            Uri = new("https://example.com/audio.flac"),
            Quality = AudioQuality.Lossless,
            Headers = new Dictionary<string, string>
            {
                ["Referer"] = "https://example.com/",
                ["X-Audio-Token"] = "test-token"
            }
        };

        Assert.Equal(AudioQuality.HiRes, await PlaybackSourceProbe.ReadFlacQualityAsync(client, source, TestContext.Current.CancellationToken));
        Assert.Equal(42, stream.BytesRead);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task PublicProbeDoesNotReadAResponseStartingAtTheWrongOffset()
    {
        var stream = new PartialReadStream(FlacHeader());
        using var client = new HttpClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(100, 141, 1000);
            return Task.FromResult(response);
        }));

        Assert.Null(await PlaybackSourceProbe.ReadFlacQualityAsync(client,
            new HttpPlaybackSource { Uri = new("https://example.com/audio.flac") }, TestContext.Current.CancellationToken));
        Assert.Equal(0, stream.BytesRead);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task PublicProbePropagatesCallerCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Cancellation should interrupt the request.");
        }));
        var probe = PlaybackSourceProbe.ReadFlacQualityAsync(client,
            new HttpPlaybackSource { Uri = new("https://example.com/audio.flac") }, cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
    }

    private static byte[] FlacHeader()
    {
        var bytes = new byte[1024]; // The ignored Range response contains more than the header.
        "fLaC"u8.CopyTo(bytes);
        bytes[4] = 0x80;
        bytes[7] = 34;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), 4096);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), 4096);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(18), (44100UL << 44) | (1UL << 41) | (23UL << 36));
        return bytes;
    }

    private sealed class PartialReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer[..Math.Min(7, buffer.Length)], cancellationToken);
            BytesRead += count;
            return count;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
