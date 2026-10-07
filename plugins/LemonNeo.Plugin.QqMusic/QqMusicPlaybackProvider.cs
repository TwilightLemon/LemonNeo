using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Exceptions;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using LemonNeo.Abstractions.Playback;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Plugin.QqMusic;

internal sealed class QqMusicPlaybackProvider(
    HttpClient httpClient,
    Func<QqMusicCredentials?> getAuthentication,
    ILogger<QqMusicPlaybackProvider> logger) :
    IMusicProvider,
    IPlaybackProvider
{
    private const string ProviderId = "qqmusic-playback";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36";
    private static readonly Uri PlaybackBaseUri = new("https://ws.stream.qqmusic.qq.com/");
    private static readonly TimeSpan QualityProbeTimeout = TimeSpan.FromSeconds(3);
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

    public async Task<PlaybackSource?> ResolvePlaybackAsync(
        MusicItemId trackId,
        PlaybackRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(trackId.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase) ||
            !IsValidSongMid(trackId.RemoteId))
        {
            return null;
        }

        var quality = SelectQuality(request.Quality);
        if (quality is null) return null;
        var authentication = getAuthentication()
            ?? throw new ProviderException(
                "QQ Music playback requires login. Run 'login qqmusic' first.");
        var purl = await ResolvePurlAsync(
            trackId.RemoteId,
            quality,
            authentication.Cookie,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(purl))
        {
            return null;
        }

        var playbackUri = Uri.TryCreate(purl, UriKind.Absolute, out var absolute)
            ? absolute
            : new Uri(PlaybackBaseUri, purl);
        if (playbackUri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var source = new HttpPlaybackSource
        {
            Quality = quality.Quality,
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
        if (quality.Extension == "flac" || playbackUri.AbsolutePath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
            source = source with { Quality = await PlaybackSourceProbe.ReadFlacQualityAsync(httpClient, source, cancellationToken) };

        logger.LogInformation(
            "Resolved QQ Music song {SongMid} with {Quality} audio (requested {RequestedQuality}).",
            trackId.RemoteId,
            source.Quality?.ToString() ?? "unknown",
            request.Quality);
        return source;
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

    private static QqQuality? SelectQuality(AudioQuality requested) => requested switch
    {
        AudioQuality.Low => QqQuality.Standard,
        AudioQuality.Medium => QqQuality.Medium,
        AudioQuality.High => QqQuality.High,
        // Request the FLAC representation once, then report its actual resolution.
        AudioQuality.Lossless or AudioQuality.HiRes or AudioQuality.Automatic => QqQuality.Lossless,
        _ => null
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

    private sealed record QqQuality(string Prefix, string Extension, string Label, AudioQuality Quality)
    {
        public static QqQuality Standard { get; } = new("M500", "mp3", "128 kbps", AudioQuality.Low);
        public static QqQuality Medium { get; } = new("C600", "m4a", "192 kbps", AudioQuality.Medium);
        public static QqQuality High { get; } = new("M800", "mp3", "320 kbps", AudioQuality.High);
        public static QqQuality Lossless { get; } = new("F000", "flac", "lossless", AudioQuality.Lossless);
    }
}
