using LemonNeo.Abstractions.Plugins;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Plugin.GdStudioNetease;

public sealed class GdStudioNeteasePlugin :
    ILemonPlugin,
    IMusicProvider,
    IPlaybackProvider
{
    private const string ProviderId = "gdstudio-netease";
    private const string PluginId = "LemonNeo.Plugin.GdStudioNetease";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/57.0.2987.110 Safari/537.36";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private HttpClient? _httpClient;
    private ILogger<GdStudioNeteasePlugin>? _logger;

    public PluginDescriptor Descriptor { get; } = new(
        PluginId,
        "GDStudio Netease Playback Source",
        "1.0.0");

    public string Id => ProviderId;

    ProviderDescriptor IMusicProvider.Descriptor { get; } = new(
        "GDStudio Netease Playback Source",
        "Resolves Netease song IDs to direct HTTP audio sources through GDStudio.");

    public IReadOnlyCollection<MusicIdScheme> AcceptedIdSchemes { get; } =
    [
        new(MusicIdNamespaces.NeteaseMusic, "song-id")
    ];

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _httpClient = context.HttpClientFactory.CreateClient(PluginId);
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
        _logger = context.LoggerFactory.CreateLogger<GdStudioNeteasePlugin>();
        context.RegisterProvider(this);
        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _httpClient?.Dispose();
        _httpClient = null;
        _logger = null;
        return ValueTask.CompletedTask;
    }

    public async Task<PlaybackSource?> ResolvePlaybackAsync(
        MusicItemId trackId,
        PlaybackRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(trackId.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase) ||
            !IsValidSongId(trackId.RemoteId))
        {
            return null;
        }

        var client = _httpClient ?? throw new InvalidOperationException("The GDStudio provider is not initialized.");
        var endpoint = new Uri(
            $"https://music-api.gdstudio.xyz/api.php?types=url&source=netease" +
            $"&id={Uri.EscapeDataString(trackId.RemoteId)}&br={RequestedBitrate(request.Quality)}");
        using var message = new HttpRequestMessage(HttpMethod.Get, endpoint);
        message.Headers.UserAgent.ParseAdd(UserAgent);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"GDStudio timed out while resolving Netease song '{trackId.RemoteId}'.",
                exception);
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();
            var payload = await JsonSerializer.DeserializeAsync<GdStudioUrlResponse>(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                JsonOptions,
                cancellationToken);
            if (payload is null ||
                !Uri.TryCreate(payload.Url, UriKind.Absolute, out var playbackUri) ||
                playbackUri.Scheme is not ("http" or "https"))
            {
                _logger?.LogWarning(
                    "GDStudio returned no usable source for Netease song {SongId}.",
                    trackId.RemoteId);
                return null;
            }

            _logger?.LogInformation(
                "Resolved Netease song {SongId} through GDStudio ({Bitrate} kbps, {Size} bytes, {Source}).",
                trackId.RemoteId,
                payload.Bitrate,
                payload.Size,
                payload.From ?? "unknown");
            return new HttpPlaybackSource
            {
                Uri = playbackUri,
                Quality = ActualQuality(payload.Bitrate),
                BitrateKbps = payload.Bitrate,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["User-Agent"] = UserAgent,
                    ["Accept"] = "*/*"
                },
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                IsDirectAudio = true
            };
        }
    }

    private static int RequestedBitrate(AudioQuality quality) => quality switch
    {
        AudioQuality.Low => 128,
        AudioQuality.Medium => 192,
        AudioQuality.High => 320,
        AudioQuality.Lossless => 740,
        _ => 999
    };

    // Response br is actual kbps, not the 740/999 request selector. Since it does not
    // include bit depth, use conservative bitrate tiers (16-bit CD PCM is 1411 kbps).
    private static AudioQuality? ActualQuality(int? bitrate) => bitrate switch
    {
        > 1411 => AudioQuality.HiRes,
        > 320 => AudioQuality.Lossless,
        > 192 => AudioQuality.High,
        > 128 => AudioQuality.Medium,
        > 0 => AudioQuality.Low,
        _ => null
    };

    private static bool IsValidSongId(string value) =>
        long.TryParse(value, out var parsed) && parsed > 0;

    private sealed record GdStudioUrlResponse
    {
        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("br")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? Bitrate { get; init; }

        [JsonPropertyName("size")]
        public long? Size { get; init; }

        [JsonPropertyName("from")]
        public string? From { get; init; }
    }
}
