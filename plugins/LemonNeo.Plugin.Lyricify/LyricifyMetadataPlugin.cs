using System.Text.Json;
using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;
using Microsoft.Extensions.Logging;
using HelperSearchers = Lyricify.Lyrics.Searchers.Searchers;
using LemonLyrics = LemonNeo.Plugin.Abstractions.Lyrics;
using LemonLyricsLine = LemonNeo.Plugin.Abstractions.LyricsLine;
using LemonTrackMetadata = LemonNeo.Plugin.Abstractions.TrackMetadata;

namespace LemonNeo.Plugin.Lyricify;

public sealed class LyricifyMetadataPlugin :
    ILemonPlugin,
    IMusicProvider,
    ISearchProvider,
    ISearchNamespaceProvider,
    ITrackLookupProvider,
    IMetadataProvider,
    ILyricsProvider
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CatalogSearchRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly SourceDefinition[] Sources =
    [
        new(MusicIdNamespaces.QQMusic, HelperSearchers.QQMusic),
        new(MusicIdNamespaces.NeteaseMusic, HelperSearchers.Netease),
        new(MusicIdNamespaces.KugouMusic, HelperSearchers.Kugou),
        new(MusicIdNamespaces.AppleMusic, HelperSearchers.AppleMusic),
        new(MusicIdNamespaces.Spotify, HelperSearchers.Spotify),
        new(MusicIdNamespaces.Musixmatch, HelperSearchers.Musixmatch),
        new(MusicIdNamespaces.SodaMusic, HelperSearchers.SodaMusic),
        new(MusicIdNamespaces.LrcLib, HelperSearchers.LRCLIB)
    ];
    private static readonly MusicIdScheme[] LyricsIdSchemes =
    [
        new(MusicIdNamespaces.QQMusic, "song-id"),
        new(MusicIdNamespaces.QQMusic, "song-mid"),
        new(MusicIdNamespaces.NeteaseMusic, "song-id"),
        new(MusicIdNamespaces.KugouMusic, "audio-hash"),
        new(MusicIdNamespaces.AppleMusic, "catalog-song-id"),
        new(MusicIdNamespaces.Spotify, "track-id"),
        new(MusicIdNamespaces.Musixmatch, "track-id"),
        new(MusicIdNamespaces.SodaMusic, "track-id"),
        new(MusicIdNamespaces.LrcLib, "lyrics-id")
    ];

    private HttpClient? _httpClient;
    private ILogger<LyricifyMetadataPlugin>? _logger;

    public PluginDescriptor Descriptor { get; } = new(
        "org.lemonneo.lyricify",
        "Lyricify Metadata & Lyrics",
        "1.0.0");

    public string Id => "lyricify";

    ProviderDescriptor IMusicProvider.Descriptor { get; } = new(
        "Lyricify Metadata & Lyrics",
        "Cross-platform search, metadata matching, and synchronized lyrics backed by Lyricify Lyrics Helper.");

    public IReadOnlyCollection<string> MetadataNamespaces { get; } =
        Sources.Select(source => source.Namespace).ToArray();

    public IReadOnlyCollection<string> SearchNamespaces => MetadataNamespaces;

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _httpClient = context.HttpClientFactory.CreateClient(Descriptor.Id);
        _httpClient.Timeout = RequestTimeout;
        _logger = context.LoggerFactory.CreateLogger<LyricifyMetadataPlugin>();
        ConfigureOptionalCredentials();
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

    private static void ConfigureOptionalCredentials()
    {
        var spotifySpDc = Environment.GetEnvironmentVariable("LEMONNEO_SPOTIFY_SP_DC");
        if (!string.IsNullOrWhiteSpace(spotifySpDc))
        {
            new global::Lyricify.Lyrics.Providers.Web.Spotify.Api().SetSpDc(spotifySpDc);
        }

        var appleMediaUserToken = Environment.GetEnvironmentVariable("LEMONNEO_APPLE_MUSIC_USER_TOKEN");
        if (!string.IsNullOrWhiteSpace(appleMediaUserToken))
        {
            new global::Lyricify.Lyrics.Providers.Web.AppleMusic.Api()
                .SetMediaUserToken(appleMediaUserToken);
        }

        var musixmatchUserToken = Environment.GetEnvironmentVariable("LEMONNEO_MUSIXMATCH_USER_TOKEN");
        if (!string.IsNullOrWhiteSpace(musixmatchUserToken))
        {
            new global::Lyricify.Lyrics.Providers.Web.Musixmatch.Api()
                .SetUserToken(musixmatchUserToken);
        }
    }

    public async Task<SearchResult> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Limit <= 0 || string.IsNullOrWhiteSpace(request.Keyword))
        {
            return new SearchResult(Id, []);
        }

        var sourceNamespace = request.SourceNamespace?.Trim();
        var catalogSources = string.IsNullOrWhiteSpace(sourceNamespace)
            ? Sources
            : Sources
                .Where(source => string.Equals(
                    source.Namespace,
                    sourceNamespace,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
        if (catalogSources.Length == 0)
        {
            return new SearchResult(Id, []);
        }

        var limitPerSource = Math.Max(1, (int)Math.Ceiling(request.Limit / (double)catalogSources.Length));
        var searches = catalogSources.Select(source =>
            SearchCatalogSourceAsync(source, request.Keyword.Trim(), limitPerSource, cancellationToken));
        var sourceResults = await Task.WhenAll(searches);
        foreach (var failed in sourceResults.Where(result => result.Error is not null))
        {
            _logger?.LogWarning(
                "Lyricify catalog search failed for {Source}: {Error}",
                failed.Namespace,
                failed.Error);
        }

        var tracks = sourceResults
            .SelectMany(result => result.Results.Select(item => ToSearchTrack(result.Namespace, item)))
            .DistinctBy(track => track.Id)
            .Take(request.Limit)
            .ToArray();
        return new SearchResult(Id, tracks);
    }

    public async Task<Track?> GetTrackAsync(
        string trackId,
        CancellationToken cancellationToken = default)
    {
        var parts = trackId.Split(':', 3);
        if (parts.Length != 3)
        {
            return null;
        }

        var sourceNamespace = parts[0];
        var kind = parts[1];
        var value = Uri.UnescapeDataString(parts[2]);
        ISearchResult? result = sourceNamespace.ToLowerInvariant() switch
        {
            MusicIdNamespaces.QQMusic when kind is "song-mid" or "song-id" =>
                await GetQqTrackAsync(value, cancellationToken),
            MusicIdNamespaces.NeteaseMusic when kind == "song-id" =>
                await GetNeteaseTrackAsync(value, cancellationToken),
            MusicIdNamespaces.KugouMusic when kind == "audio-hash" =>
                await GetKugouTrackAsync(value, cancellationToken),
            _ => null
        };
        return result is null
            ? null
            : ToSearchTrack(sourceNamespace, result) with { Id = new MusicItemId(Id, trackId) };
    }

    private static async Task<ISearchResult?> GetQqTrackAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var response = await new global::Lyricify.Lyrics.Providers.Web.QQMusic.Api()
            .GetSong(id)
            .WaitAsync(RequestTimeout, cancellationToken);
        var song = response?.Data?.FirstOrDefault();
        return song is null ? null : new QQMusicSearchResult(song);
    }

    private async Task<ISearchResult?> GetNeteaseTrackAsync(
        string id,
        CancellationToken cancellationToken)
    {
        if (!id.All(char.IsAsciiDigit))
        {
            return null;
        }

        var client = _httpClient ?? throw new InvalidOperationException("The Lyricify provider is not initialized.");
        var requestValue = Uri.EscapeDataString($"[{{\"id\":{id}}}]");
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://music.163.com/api/v3/song/detail?c={requestValue}");
        message.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        message.Headers.Referrer = new Uri("https://music.163.com/");
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("songs", out var songs) ||
            songs.ValueKind != JsonValueKind.Array ||
            songs.GetArrayLength() == 0)
        {
            return null;
        }

        var song = songs[0];
        var title = ReadJsonString(song, "name");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var artists = song.TryGetProperty("ar", out var artistValues) &&
                      artistValues.ValueKind == JsonValueKind.Array
            ? artistValues.EnumerateArray()
                .Select(artist => ReadJsonString(artist, "name"))
                .OfType<string>()
                .ToArray()
            : [];
        var album = song.TryGetProperty("al", out var albumValue) &&
                    albumValue.ValueKind == JsonValueKind.Object
            ? ReadJsonString(albumValue, "name") ?? string.Empty
            : string.Empty;
        var duration = int.TryParse(ReadJsonString(song, "dt"), out var milliseconds)
            ? milliseconds
            : 0;
        return new NeteaseSearchResult(title, artists, album, null, duration, id);
    }

    private async Task<ISearchResult?> GetKugouTrackAsync(
        string hash,
        CancellationToken cancellationToken)
    {
        var client = _httpClient ?? throw new InvalidOperationException("The Lyricify provider is not initialized.");
        var uri = new Uri(
            $"https://wwwapi.kugou.com/yy/index.php?r=play/getdata&hash={Uri.EscapeDataString(hash)}");
        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        message.Headers.Referrer = new Uri("https://www.kugou.com/");
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var title = ReadJsonString(data, "song_name");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var artists = (ReadJsonString(data, "author_name") ?? string.Empty)
            .Split(['、', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var duration = int.TryParse(ReadJsonString(data, "timelength"), out var milliseconds)
            ? milliseconds
            : 0;
        return new KugouSearchResult(
            title,
            artists,
            ReadJsonString(data, "album_name") ?? string.Empty,
            null,
            duration,
            hash);
    }

    private static string? ReadJsonString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var child) && child.ValueKind != JsonValueKind.Null
            ? child.ValueKind == JsonValueKind.String ? child.GetString() : child.ToString()
            : null;

    private static async Task<CatalogSearchResult> SearchCatalogSourceAsync(
        SourceDefinition source,
        string keyword,
        int limit,
        CancellationToken cancellationToken)
    {
        var attempts = source.Searcher == HelperSearchers.Netease ? 2 : 1;
        string? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var results = await source.Searcher.GetSearcher()
                    .SearchForResults(keyword)
                    .WaitAsync(RequestTimeout, cancellationToken);
                if (results is not null)
                {
                    return new CatalogSearchResult(
                        source.Namespace,
                        results.Take(limit).ToArray(),
                        null);
                }

                lastError = "The searcher returned no response.";
            }
            catch (TimeoutException exception)
            {
                return new CatalogSearchResult(source.Namespace, [], exception.Message);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastError = exception.Message;
            }

            if (attempt < attempts)
            {
                await Task.Delay(CatalogSearchRetryDelay, cancellationToken);
            }
        }

        return new CatalogSearchResult(
            source.Namespace,
            [],
            $"Search failed after {attempts} attempt(s): {lastError}");
    }

    private static Track ToSearchTrack(string sourceNamespace, ISearchResult result)
    {
        var identifiers = GetIdentifiers(sourceNamespace, result);
        var primaryId = identifiers.FirstOrDefault(identifier =>
                            string.Equals(identifier.Kind, "song-mid", StringComparison.OrdinalIgnoreCase))
                        ?? identifiers.First();
        return new Track
        {
            Id = new MusicItemId(
                "lyricify",
                $"{sourceNamespace}:{primaryId.Kind}:{Uri.EscapeDataString(primaryId.Value)}"),
            Title = result.Title,
            Artists = result.Artists.Select(artist => new ArtistRef { Name = artist }).ToArray(),
            Album = string.IsNullOrWhiteSpace(result.Album)
                ? null
                : new AlbumRef { Title = result.Album },
            Duration = result.DurationMs is null
                ? null
                : TimeSpan.FromMilliseconds(result.DurationMs.Value),
            ExternalIds = identifiers
        };
    }

    public async Task<MetadataLookupResult> LookupMetadataAsync(
        MetadataLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        var requestedNamespaces = request.TargetNamespaces.Count == 0
            ? null
            : request.TargetNamespaces.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var searches = Sources
            .Where(source => requestedNamespaces is null || requestedNamespaces.Contains(source.Namespace))
            .Select(source => SearchSourceAsync(source, request, cancellationToken));
        var sourceResults = await Task.WhenAll(searches);

        var matches = sourceResults.SelectMany(result => result.Matches)
            .OrderByDescending(match => match.Confidence)
            .ThenBy(match => match.SourceNamespace, StringComparer.Ordinal)
            .ToArray();
        var errors = sourceResults
            .Where(result => result.Error is not null)
            .ToDictionary(result => result.Namespace, result => result.Error!, StringComparer.OrdinalIgnoreCase);
        return new MetadataLookupResult(matches, errors);
    }

    public async Task<LemonLyrics?> GetLyricsAsync(
        Track track,
        CancellationToken cancellationToken = default)
    {
        var identifiers = track.ExternalIds.Where(IsSupportedLyricsIdentifier).ToList();
        if (identifiers.Count == 0)
        {
            var lookup = await LookupMetadataAsync(
                new MetadataLookupRequest
                {
                    Track = track,
                    TargetNamespaces =
                    [
                        MusicIdNamespaces.AppleMusic,
                        MusicIdNamespaces.Spotify,
                        MusicIdNamespaces.NeteaseMusic,
                        MusicIdNamespaces.QQMusic,
                        MusicIdNamespaces.SodaMusic,
                        MusicIdNamespaces.LrcLib,
                        MusicIdNamespaces.KugouMusic,
                        MusicIdNamespaces.Musixmatch
                    ],
                    LimitPerSource = 1,
                    MinimumConfidence = 0.7
                },
                cancellationToken);
            identifiers.AddRange(lookup.Matches.SelectMany(match => match.Metadata.ExternalIds));
        }

        var preferences = new[]
        {
            MusicIdNamespaces.QQMusic,
            MusicIdNamespaces.NeteaseMusic,
            MusicIdNamespaces.AppleMusic,
            MusicIdNamespaces.Spotify,
            MusicIdNamespaces.SodaMusic,
            MusicIdNamespaces.LrcLib,
            MusicIdNamespaces.KugouMusic,
            MusicIdNamespaces.Musixmatch
        };
        foreach (var sourceNamespace in preferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceIds = identifiers
                .Where(id => string.Equals(id.Namespace, sourceNamespace, StringComparison.OrdinalIgnoreCase) &&
                             IsSupportedLyricsIdentifier(id))
                .ToArray();
            if (sourceIds.Length == 0)
            {
                continue;
            }

            try
            {
                var lyrics = await FetchLyricsAsync(sourceNamespace, sourceIds, track, cancellationToken);
                if (lyrics is not null)
                {
                    return lyrics;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger?.LogWarning(
                    exception,
                    "Failed to fetch {Source} lyrics for {Track}.",
                    sourceNamespace,
                    track.Title);
            }
        }

        return null;
    }

    private static async Task<SourceSearchResult> SearchSourceAsync(
        SourceDefinition source,
        MetadataLookupRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var matches = await SearchMetadataMatchesAsync(source.Searcher.GetSearcher(), source.Namespace, request, cancellationToken);
            return new SourceSearchResult(source.Namespace, matches, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new SourceSearchResult(source.Namespace, [], exception.Message);
        }
    }

    internal static async Task<IReadOnlyList<MetadataMatch>> SearchMetadataMatchesAsync(
        ISearcher searcher,
        string sourceNamespace,
        MetadataLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var helperTrack = ToHelperMetadata(request.Track);
        var customKeyword = !string.IsNullOrWhiteSpace(request.SearchKeyword);
        var results = customKeyword
            ? await searcher.SearchForResults(request.SearchKeyword!.Trim()).WaitAsync(RequestTimeout, cancellationToken) ?? []
            : await searcher.SearchForResults(helperTrack).WaitAsync(RequestTimeout, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return results.Select(result => ToMetadataMatch(sourceNamespace, result,
                customKeyword ? CompareHelper.CompareTrack(helperTrack, result) : null))
            .Where(match => match.Confidence >= request.MinimumConfidence)
            .OrderByDescending(match => match.Confidence)
            .Take(Math.Max(0, request.LimitPerSource)).ToArray();
    }

    private static MetadataMatch ToMetadataMatch(string sourceNamespace, ISearchResult result, CompareHelper.MatchType? matchType = null)
    {
        var level = matchType ?? result.MatchType ?? CompareHelper.MatchType.NoMatch;
        return new MetadataMatch
        {
            MetadataProviderId = "lyricify",
            SourceNamespace = sourceNamespace,
            Confidence = Confidence(level),
            MatchLevel = level.ToString(),
            Metadata = new LemonTrackMetadata
            {
                Title = result.Title,
                Artists = result.Artists,
                Album = string.IsNullOrWhiteSpace(result.Album) ? null : result.Album,
                Duration = result.DurationMs is null ? null : TimeSpan.FromMilliseconds(result.DurationMs.Value),
                ExternalIds = GetIdentifiers(sourceNamespace, result)
            }
        };
    }

    private static IReadOnlyList<ExternalMusicId> GetIdentifiers(string sourceNamespace, ISearchResult result)
    {
        return result switch
        {
            QQMusicSearchResult qq =>
            [
                new(sourceNamespace, qq.Id, "song-id"),
                new(sourceNamespace, qq.Mid, "song-mid")
            ],
            NeteaseSearchResult netease => [new(sourceNamespace, netease.Id, "song-id")],
            KugouSearchResult kugou => [new(sourceNamespace, kugou.Hash, "audio-hash")],
            AppleMusicSearchResult apple => [new(sourceNamespace, apple.Id, "catalog-song-id")],
            SpotifySearchResult spotify => [new(sourceNamespace, spotify.Id, "track-id")],
            MusixmatchSearchResult musixmatch => CreateMusixmatchIds(sourceNamespace, musixmatch),
            SodaMusicSearchResult soda => [new(sourceNamespace, soda.Id, "track-id")],
            LRCLIBSearchResult lrcLib => [new(sourceNamespace, lrcLib.Id.ToString(), "lyrics-id")],
            _ => []
        };
    }

    private static IReadOnlyList<ExternalMusicId> CreateMusixmatchIds(
        string sourceNamespace,
        MusixmatchSearchResult result)
    {
        var ids = new List<ExternalMusicId>
        {
            new(sourceNamespace, result.Id.ToString(), "track-id")
        };
        if (!string.IsNullOrWhiteSpace(result.Isrc))
        {
            ids.Add(new ExternalMusicId(MusicIdNamespaces.Isrc, result.Isrc));
        }

        return ids;
    }

    private static TrackMultiArtistMetadata ToHelperMetadata(Track track) => new()
    {
        Title = track.Title,
        Artists = track.Artists.Select(artist => artist.Name).ToList(),
        Album = track.Album?.Title,
        DurationMs = track.Duration is null ? null : (int)track.Duration.Value.TotalMilliseconds,
        Isrc = track.ExternalIds.FirstOrDefault(id =>
            string.Equals(id.Namespace, MusicIdNamespaces.Isrc, StringComparison.OrdinalIgnoreCase))?.Value
    };

    private static async Task<LemonLyrics?> FetchLyricsAsync(
        string sourceNamespace,
        IReadOnlyList<ExternalMusicId> ids,
        Track track,
        CancellationToken cancellationToken) => sourceNamespace switch
        {
            MusicIdNamespaces.AppleMusic => await FetchAppleLyricsAsync(ids, cancellationToken),
            MusicIdNamespaces.Spotify => await FetchSpotifyLyricsAsync(ids, cancellationToken),
            MusicIdNamespaces.NeteaseMusic => await FetchNeteaseLyricsAsync(ids, cancellationToken),
            MusicIdNamespaces.QQMusic => await FetchQqLyricsAsync(ids, cancellationToken),
            MusicIdNamespaces.SodaMusic => await FetchSodaLyricsAsync(ids, cancellationToken),
            MusicIdNamespaces.LrcLib => await FetchLrcLibLyricsAsync(ids, cancellationToken),
            MusicIdNamespaces.KugouMusic => await FetchKugouLyricsAsync(ids, track, cancellationToken),
            MusicIdNamespaces.Musixmatch => await FetchMusixmatchLyricsAsync(ids, cancellationToken),
            _ => null
        };

    private static async Task<LemonLyrics?> FetchAppleLyricsAsync(
        IReadOnlyList<ExternalMusicId> ids,
        CancellationToken cancellationToken)
    {
        var id = Find(ids, "catalog-song-id");
        if (id is null) return null;
        var response = await new global::Lyricify.Lyrics.Providers.Web.AppleMusic.Api()
            .GetLyrics(id.Value)
            .WaitAsync(RequestTimeout, cancellationToken);
        return string.IsNullOrWhiteSpace(response?.Ttml)
            ? null
            : ParseLyrics(response.Ttml, LyricsRawTypes.Ttml, MusicIdNamespaces.AppleMusic, id);
    }

    private static async Task<LemonLyrics?> FetchSpotifyLyricsAsync(
        IReadOnlyList<ExternalMusicId> ids,
        CancellationToken cancellationToken)
    {
        var id = Find(ids, "track-id");
        if (id is null) return null;
        var raw = await new global::Lyricify.Lyrics.Providers.Web.Spotify.Api()
            .GetLyrics(id.Value)
            .WaitAsync(RequestTimeout, cancellationToken);
        return string.IsNullOrWhiteSpace(raw)
            ? null
            : ParseLyrics(raw, LyricsRawTypes.Spotify, MusicIdNamespaces.Spotify, id);
    }

    private static async Task<LemonLyrics?> FetchNeteaseLyricsAsync(
        IReadOnlyList<ExternalMusicId> ids,
        CancellationToken cancellationToken)
    {
        var id = Find(ids, "song-id");
        if (id is null) return null;
        var result = await new global::Lyricify.Lyrics.Providers.Web.Netease.Api()
            .GetLyricNew(id.Value)
            .WaitAsync(RequestTimeout, cancellationToken);
        var raw = result?.Yrc?.Lyric;
        var type = LyricsRawTypes.Yrc;
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = result?.Lrc?.Lyric;
            type = LyricsRawTypes.Lrc;
        }

        if (string.IsNullOrWhiteSpace(raw)) return null;
        var translation = FirstNonEmpty(
            type == LyricsRawTypes.Yrc ? result?.Ytlrc?.Lyric : null, result?.Tlyric?.Lyric);
        var romanization = FirstNonEmpty(
            type == LyricsRawTypes.Yrc ? result?.Yromalrc?.Lyric : null, result?.Romalrc?.Lyric);
        return ParseLyrics(raw, type, MusicIdNamespaces.NeteaseMusic, id, translation, romanization);
    }

    private static async Task<LemonLyrics?> FetchQqLyricsAsync(
        IReadOnlyList<ExternalMusicId> ids,
        CancellationToken cancellationToken)
    {
        var api = new global::Lyricify.Lyrics.Providers.Web.QQMusic.Api();
        var numericId = Find(ids, "song-id");
        if (numericId is not null)
        {
            var qrc = await api.GetLyricsAsync(numericId.Value).WaitAsync(RequestTimeout, cancellationToken);
            if (!string.IsNullOrWhiteSpace(qrc?.Lyrics))
            {
                return ParseLyrics(
                    qrc.Lyrics,
                    LyricsRawTypes.Qrc,
                    MusicIdNamespaces.QQMusic,
                    numericId,
                    qrc.Trans,
                    qrc.Romaji);
            }
        }

        var mid = Find(ids, "song-mid");
        if (mid is null) return null;
        var lrc = await api.GetLyric(mid.Value).WaitAsync(RequestTimeout, cancellationToken);
        return string.IsNullOrWhiteSpace(lrc?.Lyric)
            ? null
            : ParseLyrics(lrc.Lyric, LyricsRawTypes.Lrc, MusicIdNamespaces.QQMusic, mid, lrc.Trans);
    }

    private static async Task<LemonLyrics?> FetchSodaLyricsAsync(
        IReadOnlyList<ExternalMusicId> ids,
        CancellationToken cancellationToken)
    {
        var id = Find(ids, "track-id");
        if (id is null) return null;
        var detail = await new global::Lyricify.Lyrics.Providers.Web.SodaMusic.Api()
            .GetDetail(id.Value)
            .WaitAsync(RequestTimeout, cancellationToken);
        var source = detail?.Lyric;
        if (string.IsNullOrWhiteSpace(source?.Content)) return null;

        var rawType = Enum.TryParse<LyricsRawTypes>(source.Type, true, out var declaredType)
            ? declaredType
            : TypeHelper.GetLyricsTypes(source.Content);
        var translation = source.Translations?.ChineseTranslation
                          ?? source.LangTranslations?.Values
                              .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value.Content))?.Content;
        return ParseLyrics(
            source.Content,
            rawType,
            MusicIdNamespaces.SodaMusic,
            id,
            translation) with
        {
            Language = string.IsNullOrWhiteSpace(source.Lang) ? null : source.Lang
        };
    }

    private static async Task<LemonLyrics?> FetchLrcLibLyricsAsync(
        IReadOnlyList<ExternalMusicId> ids,
        CancellationToken cancellationToken)
    {
        var id = Find(ids, "lyrics-id");
        if (id is null || !int.TryParse(id.Value, out var numericId)) return null;
        var result = await new global::Lyricify.Lyrics.Providers.Web.LRCLIB.Api()
            .GetById(numericId)
            .WaitAsync(RequestTimeout, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result?.SyncedLyrics))
        {
            return ParseLyrics(result.SyncedLyrics, LyricsRawTypes.Lrc, MusicIdNamespaces.LrcLib, id);
        }

        return string.IsNullOrWhiteSpace(result?.PlainLyrics)
            ? null
            : CreatePlainLyrics(result.PlainLyrics, MusicIdNamespaces.LrcLib, id);
    }

    private static async Task<LemonLyrics?> FetchKugouLyricsAsync(
        IReadOnlyList<ExternalMusicId> ids,
        Track track,
        CancellationToken cancellationToken)
    {
        var hash = Find(ids, "audio-hash");
        if (hash is null) return null;
        var api = new global::Lyricify.Lyrics.Providers.Web.Kugou.Api();
        var search = await api.GetSearchLyrics(
                track.Title,
                track.Duration is null ? null : (int)track.Duration.Value.TotalMilliseconds,
                hash.Value)
            .WaitAsync(RequestTimeout, cancellationToken);
        var candidate = search?.Candidates?.FirstOrDefault();
        if (candidate is null) return null;
        var raw = await global::Lyricify.Lyrics.Decrypter.Krc.Helper
            .GetLyricsAsync(candidate.Id, candidate.AccessKey)
            .WaitAsync(RequestTimeout, cancellationToken);
        return string.IsNullOrWhiteSpace(raw)
            ? null
            : ParseLyrics(raw, LyricsRawTypes.Krc, MusicIdNamespaces.KugouMusic, hash);
    }

    private static async Task<LemonLyrics?> FetchMusixmatchLyricsAsync(
        IReadOnlyList<ExternalMusicId> ids,
        CancellationToken cancellationToken)
    {
        var id = Find(ids, "track-id");
        if (id is null) return null;
        var raw = await new global::Lyricify.Lyrics.Providers.Web.Musixmatch.Api()
            .GetFullLyricsRaw(id.Value)
            .WaitAsync(RequestTimeout, cancellationToken);
        return string.IsNullOrWhiteSpace(raw)
            ? null
            : ParseLyrics(raw, LyricsRawTypes.Musixmatch, MusicIdNamespaces.Musixmatch, id);
    }

    private static LemonLyrics ParseLyrics(
        string raw,
        LyricsRawTypes type,
        string source,
        ExternalMusicId sourceId,
        string? translation = null,
        string? romanization = null) =>
        LyricsConverter.Parse(raw, type, source, sourceId, translation, romanization);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static LemonLyrics CreatePlainLyrics(
        string text,
        string source,
        ExternalMusicId sourceId,
        string? translation = null) => new(
        text,
        IsSynchronized: false,
        Lines: text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => new LemonLyricsLine(line))
            .ToArray(),
        Source: source,
        SourceId: sourceId,
        Translation: translation);

    private static ExternalMusicId? Find(IEnumerable<ExternalMusicId> ids, string kind) =>
        ids.FirstOrDefault(id => string.Equals(id.Kind, kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Accepts identifiers by the platform schema consumed by the lyrics API.
    /// Namespace and kind distinguish values with different platform semantics.
    /// </summary>
    private static bool IsSupportedLyricsIdentifier(ExternalMusicId id) =>
        LyricsIdSchemes.Any(scheme => scheme.Accepts(id));

    private static double Confidence(CompareHelper.MatchType? matchType) =>
        Math.Clamp(((int)(matchType ?? CompareHelper.MatchType.NoMatch)) / 100d, 0, 1);

    private sealed record SourceDefinition(string Namespace, HelperSearchers Searcher);

    private sealed record SourceSearchResult(
        string Namespace,
        IReadOnlyList<MetadataMatch> Matches,
        string? Error);

    private sealed record CatalogSearchResult(
        string Namespace,
        IReadOnlyList<ISearchResult> Results,
        string? Error);
}
