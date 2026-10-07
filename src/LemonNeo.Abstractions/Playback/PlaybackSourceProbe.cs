using System.Net.Http.Headers;
using LemonNeo.Core.Playback;

namespace LemonNeo.Abstractions.Playback;

/// <summary>Inspects audio resources without downloading the complete file.</summary>
public static class PlaybackSourceProbe
{
    /// <summary>
    /// Reads the first 42 bytes of a FLAC resource using the supplied client and source headers.
    /// Reports HiRes for bit depths above 16 or sample rates above 48 kHz, otherwise Lossless.
    /// Returns null for unavailable or invalid headers, including a three-second probe timeout.
    /// Cancellation requested by the caller is propagated.
    /// </summary>
    public static async Task<AudioQuality?> ReadFlacQualityAsync(
        HttpClient httpClient,
        HttpPlaybackSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var message = new HttpRequestMessage(HttpMethod.Get, source.Uri);
        foreach (var header in source.Headers) message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        message.Headers.Range = new RangeHeaderValue(0, 41);
        try
        {
            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentRange?.From is > 0) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var header = new byte[42];
            // A server may ignore Range. Never buffer or read its complete response.
            await stream.ReadExactlyAsync(header, timeout.Token);
            if (!header.AsSpan(0, 4).SequenceEqual("fLaC"u8) || (header[4] & 0x7f) != 0 ||
                header[5] != 0 || header[6] != 0 || header[7] != 34) return null;

            // RFC 9639 section 8.2: 20-bit sample rate and 5-bit (bits-per-sample - 1).
            var sampleRate = (header[18] << 12) | (header[19] << 4) | (header[20] >> 4);
            var bitDepth = (((header[20] & 1) << 4) | (header[21] >> 4)) + 1;
            if (sampleRate == 0 || bitDepth < 4) return null;
            return AudioQualities.Classify(new AudioStreamInfo(SampleRateHz: sampleRate, BitsPerSample: bitDepth, IsLossless: true));
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested &&
            exception is HttpRequestException or IOException or OperationCanceledException)
        {
            return null;
        }
    }
}
