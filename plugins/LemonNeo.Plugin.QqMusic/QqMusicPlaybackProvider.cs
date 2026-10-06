using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Exceptions;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Plugin.QqMusic;

internal sealed class QqMusicPlaybackProvider(
    HttpClient httpClient,
    Func<QqMusicCredentials?> getAuthentication,
    ILogger<QqMusicPlaybackProvider> logger) :
    IMusicProvider,
    IPlaybackProvider,
    ITrackIdResolverProvider
{
    private const string ProviderId = "qqmusic-playback";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36";
    private static readonly Uri PlaybackBaseUri = new("https://ws.stream.qqmusic.qq.com/");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public string Id => ProviderId;

    public ProviderDescriptor Descriptor { get; } = new(
        "QQ Music Playback Source",
        "Resolves QQ Music song MIDs to authenticated vkey audio URLs.");

    public IReadOnlyCollection<MusicIdScheme> AcceptedIdSchemes { get; } =
    [
        new(MusicIdNamespaces.QQMusic, "song-mid")
    ];

    public Task<Track?> ResolveTrackIdAsync(
        ExternalMusicId identifier,
        TrackMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AcceptedIdSchemes.Any(scheme => scheme.Accepts(identifier)) ||
            !IsValidSongMid(identifier.Value))
        {
            return Task.FromResult<Track?>(null);
        }

        var externalIds = metadata.ExternalIds.Append(identifier)
            .DistinctBy(id => new
            {
                Namespace = id.Namespace.ToUpperInvariant(),
                id.Value,
                Kind = id.Kind?.ToUpperInvariant()
            })
            .ToArray();
        return Task.FromResult<Track?>(new Track
        {
            Id = new MusicItemId(ProviderId, identifier.Value),
            Title = metadata.Title,
            Artists = metadata.Artists.Select(name => new ArtistRef { Name = name }).ToArray(),
            Album = string.IsNullOrWhiteSpace(metadata.Album)
                ? null
                : new AlbumRef { Title = metadata.Album },
            Duration = metadata.Duration,
            ArtworkUri = metadata.ArtworkUri,
            ExternalIds = externalIds
        });
    }

    public async Task<PlaybackSource?> ResolvePlaybackAsync(
        Track track,
        PlaybackRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(track.Id.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase) ||
            !IsValidSongMid(track.Id.RemoteId))
        {
            return null;
        }

        var authentication = getAuthentication()
            ?? throw new ProviderException(
                "QQ Music playback requires login. Run 'login qqmusic' first.");
        foreach (var quality in SelectQualities(request.Quality))
        {
            var purl = await ResolvePurlAsync(
                track.Id.RemoteId,
                quality,
                authentication.Cookie,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(purl))
            {
                continue;
            }

            var playbackUri = Uri.TryCreate(purl, UriKind.Absolute, out var absolute)
                ? absolute
                : new Uri(PlaybackBaseUri, purl);
            if (playbackUri.Scheme is not ("http" or "https"))
            {
                continue;
            }

            logger.LogInformation(
                "Resolved QQ Music song {SongMid} at {Quality} quality.",
                track.Id.RemoteId,
                quality.Label);
            return new HttpPlaybackSource
            {
                CacheKey = $"{Id}:{track.Id.RemoteId}:{quality.Prefix}:{quality.Extension}",
                Uri = playbackUri,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["User-Agent"] = UserAgent,
                    ["Referer"] = "https://y.qq.com/",
                    ["Accept"] = "*/*"
                },
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                IsDirectAudio = true
            };
        }

        logger.LogWarning(
            "QQ Music returned no playable vkey source for song {SongMid}.",
            track.Id.RemoteId);
        return null;
    }

    private async Task<string?> ResolvePurlAsync(
        string songMid,
        QqQuality quality,
        string cookie,
        CancellationToken cancellationToken)
    {
        var filename = $"{quality.Prefix}{songMid}{songMid}.{quality.Extension}";
        var payload = JsonSerializer.Serialize(new
        {
            req_0 = new
            {
                module = "vkey.GetVkeyServer",
                method = "CgiGetVkey",
                param = new
                {
                    filename = new[] { filename },
                    guid = "10000",
                    songmid = new[] { songMid },
                    songtype = new[] { 0 },
                    uin = "0",
                    loginflag = 1,
                    platform = "20"
                }
            },
            loginUin = "0",
            comm = new
            {
                uin = "0",
                format = "json",
                ct = 24,
                cv = 0
            }
        }, JsonOptions);
        var endpoint = new Uri(
            $"https://u.y.qq.com/cgi-bin/musicu.fcg?format=json&data={Uri.EscapeDataString(payload)}");
        using var message = new HttpRequestMessage(HttpMethod.Get, endpoint);
        message.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        message.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        message.Headers.TryAddWithoutValidation("Cookie", cookie);
        message.Headers.Referrer = new Uri("https://y.qq.com/");
        message.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"QQ Music timed out while resolving song '{songMid}'.",
                exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new ProviderException(
                    $"QQ Music playback request failed with HTTP {(int)response.StatusCode} " +
                    $"({response.ReasonPhrase}).");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!TryGetObject(document.RootElement, "req_0", out var requestResult))
            {
                throw new ProviderException("QQ Music playback response did not contain req_0.");
            }

            if (ReadInt32(requestResult, "code") is { } code && code != 0)
            {
                throw new ProviderException($"QQ Music playback API returned code {code}.");
            }

            if (!TryGetObject(requestResult, "data", out var data) ||
                !data.TryGetProperty("midurlinfo", out var urlInfo) ||
                urlInfo.ValueKind != JsonValueKind.Array ||
                urlInfo.GetArrayLength() == 0)
            {
                return null;
            }

            return ReadString(urlInfo[0], "purl");
        }
    }

    private static IReadOnlyList<QqQuality> SelectQualities(AudioQuality requested) => requested switch
    {
        AudioQuality.Low or AudioQuality.Medium => [QqQuality.Standard],
        AudioQuality.High => [QqQuality.High, QqQuality.Standard],
        AudioQuality.Lossless or AudioQuality.Automatic =>
            [QqQuality.Lossless, QqQuality.High, QqQuality.Standard],
        _ => [QqQuality.Standard]
    };

    private static bool IsValidSongMid(string value) =>
        value.Length is > 0 and <= 128 && value.All(char.IsAsciiLetterOrDigit);

    private static bool TryGetObject(JsonElement value, string property, out JsonElement result)
    {
        if (value.TryGetProperty(property, out result) && result.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        result = default;
        return false;
    }

    private static string? ReadString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var child) || child.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return child.ValueKind == JsonValueKind.String ? child.GetString() : child.ToString();
    }

    private static int? ReadInt32(JsonElement value, string property) =>
        int.TryParse(ReadString(value, property), out var result) ? result : null;

    private sealed record QqQuality(string Prefix, string Extension, string Label)
    {
        public static QqQuality Standard { get; } = new("C400", "m4a", "standard");
        public static QqQuality High { get; } = new("M800", "mp3", "high");
        public static QqQuality Lossless { get; } = new("F000", "flac", "lossless");
    }
}
