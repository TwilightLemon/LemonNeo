using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Plugin.GdStudioNetease;

public sealed class GdStudioNeteasePlugin :
    ILemonPlugin,
    IMusicProvider,
    IPlaybackProvider,
    ITrackIdResolverProvider
{
    private const string ProviderId = "gdstudio-netease";
    private const string OwnAuthority = "org.lemonneo.gdstudio.netease";
    private const string LyricifyAuthority = "org.lyricify.helper/netease";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/57.0.2987.110 Safari/537.36";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private HttpClient? _httpClient;
    private ILogger<GdStudioNeteasePlugin>? _logger;

    public PluginDescriptor Descriptor { get; } = new(
        OwnAuthority,
        "GDStudio Netease Playback Source",
        "1.0.0");

    public string Id => ProviderId;

    ProviderDescriptor IMusicProvider.Descriptor { get; } = new(
        "GDStudio Netease Playback Source",
        "Resolves Netease song IDs to direct HTTP audio sources through GDStudio.");

    public IReadOnlyCollection<MusicIdScheme> AcceptedIdSchemes { get; } =
    [
        new(MusicIdNamespaces.NeteaseMusic, LyricifyAuthority, "song-id"),
        new(MusicIdNamespaces.NeteaseMusic, OwnAuthority, "song-id")
    ];

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _httpClient = context.HttpClientFactory.CreateClient(OwnAuthority);
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

    public Task<TrackReference?> ResolveTrackIdAsync(
        ExternalMusicId identifier,
        TrackMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AcceptedIdSchemes.Any(scheme => scheme.Accepts(identifier)) || !IsValidSongId(identifier.Value))
        {
            return Task.FromResult<TrackReference?>(null);
        }

        var ownIdentifier = new ExternalMusicId(
            MusicIdNamespaces.NeteaseMusic,
            identifier.Value,
            OwnAuthority,
            "song-id");
        var externalIds = metadata.ExternalIds.Append(identifier).Append(ownIdentifier)
            .DistinctBy(id => new
            {
                Namespace = id.Namespace.ToUpperInvariant(),
                id.Value,
                Authority = id.Authority?.ToUpperInvariant(),
                Kind = id.Kind?.ToUpperInvariant()
            })
            .ToArray();
        return Task.FromResult<TrackReference?>(new TrackReference
        {
            Id = new MusicItemId(ProviderId, identifier.Value),
            Title = metadata.Title,
            Artists = metadata.Artists.Select(name => new ArtistRef { Name = name }).ToArray(),
            Album = string.IsNullOrWhiteSpace(metadata.Album)
                ? null
                : new AlbumRef { Title = metadata.Album },
            Duration = metadata.Duration,
            ExternalIds = externalIds
        });
    }

    public async Task<PlaybackSource?> ResolvePlaybackAsync(
        TrackReference track,
        PlaybackRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(track.Id.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase) ||
            !IsValidSongId(track.Id.RemoteId))
        {
            return null;
        }

        var client = _httpClient ?? throw new InvalidOperationException("The GDStudio provider is not initialized.");
        var endpoint = new Uri(
            $"https://music-api.gdstudio.xyz/api.php?types=url&source=netease" +
            $"&id={Uri.EscapeDataString(track.Id.RemoteId)}&br=999");
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
                $"GDStudio timed out while resolving Netease song '{track.Id.RemoteId}'.",
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
                    track.Id.RemoteId);
                return null;
            }

            _logger?.LogInformation(
                "Resolved Netease song {SongId} through GDStudio ({Bitrate} kbps, {Size} bytes, {Source}).",
                track.Id.RemoteId,
                payload.Bitrate,
                payload.Size,
                payload.From ?? "unknown");
            return new HttpPlaybackSource
            {
                Uri = playbackUri,
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

    private static bool IsValidSongId(string value) =>
        long.TryParse(value, out var parsed) && parsed > 0;

    private sealed record GdStudioUrlResponse
    {
        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("br")]
        public int? Bitrate { get; init; }

        [JsonPropertyName("size")]
        public long? Size { get; init; }

        [JsonPropertyName("from")]
        public string? From { get; init; }
    }
}
