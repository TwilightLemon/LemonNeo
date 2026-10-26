using System.Text.Json;
using LemonNeo.Abstractions.Plugins;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Music;
using Lyricify.Lyrics.Decrypter.Krc;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Providers.Web.Spotify;
using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;
using Microsoft.Extensions.Logging;
using HelperSearchers = Lyricify.Lyrics.Searchers.Searchers;
using LemonLyrics = LemonNeo.Core.Lyrics.Lyrics;
using LemonLyricsLine = LemonNeo.Core.Lyrics.LyricsLine;

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

    private HttpClient? _httpClient;
    private ILogger<LyricifyMetadataPlugin>? _logger;

    public PluginDescriptor Descriptor { get; } = new(
        "LemonNeo.Plugin.Lyricify",
        "Lyricify Metadata & Lyrics",
        "1.0.0");

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

    public IReadOnlyCollection<string> MetadataNamespaces { get; } =
        Sources.Select(source => source.Namespace).ToArray();

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

    public string Id => "lyricify";

    ProviderDescriptor IMusicProvider.Descriptor { get; } = new(
        "Lyricify Metadata & Lyrics",
        "Cross-platform search, metadata matching and lyrics by known IDs backed by Lyricify Lyrics Helper.");

    public IReadOnlyCollection<MusicIdScheme> AcceptedIdSchemes { get; } =
    [
        new(MusicIdNamespaces.QQMusic, "song-id"),
        new(MusicIdNamespaces.NeteaseMusic, "song-id"),
        new(MusicIdNamespaces.KugouMusic, "audio-hash"),
        new(MusicIdNamespaces.AppleMusic, "catalog-song-id"),
        new(MusicIdNamespaces.Spotify, "track-id"),
        new(MusicIdNamespaces.Musixmatch, "track-id"),
        new(MusicIdNamespaces.SodaMusic, "track-id"),
        new(MusicIdNamespaces.LrcLib, "lyrics-id")
    ];

    public Task<LemonLyrics?> GetLyricsAsync(ExternalMusicId musicId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scheme = AcceptedIdSchemes.FirstOrDefault(candidate => candidate.Accepts(musicId));
        if (scheme is null || string.IsNullOrWhiteSpace(musicId.Value))
            return Task.FromResult<LemonLyrics?>(null);
        var id = new ExternalMusicId(scheme.Namespace, musicId.Value, scheme.Kind);
        return scheme.Namespace switch
        {
            MusicIdNamespaces.QQMusic => FetchQqAsync(id, cancellationToken),
            MusicIdNamespaces.NeteaseMusic => FetchNeteaseAsync(id, cancellationToken),
            MusicIdNamespaces.KugouMusic => FetchKugouAsync(id, cancellationToken),
            MusicIdNamespaces.AppleMusic => FetchAppleAsync(id, cancellationToken),
            MusicIdNamespaces.Spotify => FetchSpotifyAsync(id, cancellationToken),
            MusicIdNamespaces.Musixmatch => FetchMusixmatchAsync(id, cancellationToken),
            MusicIdNamespaces.SodaMusic => FetchSodaAsync(id, cancellationToken),
            MusicIdNamespaces.LrcLib => FetchLrcLibAsync(id, cancellationToken),
            _ => Task.FromResult<LemonLyrics?>(null)
        };
    }

    private static async Task<LemonLyrics?> FetchQqAsync(ExternalMusicId id, CancellationToken token)
    {
        if (!long.TryParse(id.Value, out var number) || number <= 0) return null;
        var result = await new global::Lyricify.Lyrics.Providers.Web.QQMusic.Api()
            .GetLyricsAsync(id.Value).WaitAsync(RequestTimeout, token);
        return string.IsNullOrWhiteSpace(result?.Lyrics) ? null
            : Parse(result.Lyrics, LyricsRawTypes.Qrc, id, result.Trans, result.Romaji);
    }

    private static async Task<LemonLyrics?> FetchNeteaseAsync(ExternalMusicId id, CancellationToken token)
    {
        var result = await new global::Lyricify.Lyrics.Providers.Web.Netease.Api()
            .GetLyricNew(id.Value).WaitAsync(RequestTimeout, token);
        var raw = result?.Yrc?.Lyric;
        var type = LyricsRawTypes.Yrc;
        if (string.IsNullOrWhiteSpace(raw)) { raw = result?.Lrc?.Lyric; type = LyricsRawTypes.Lrc; }
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var translation = FirstNonEmpty(type == LyricsRawTypes.Yrc ? result?.Ytlrc?.Lyric : null, result?.Tlyric?.Lyric);
        var romanization = FirstNonEmpty(type == LyricsRawTypes.Yrc ? result?.Yromalrc?.Lyric : null, result?.Romalrc?.Lyric);
        return Parse(raw, type, id, translation, romanization);
    }

    private static async Task<LemonLyrics?> FetchKugouAsync(ExternalMusicId id, CancellationToken token)
    {
        // Resolve download credentials using this recording's hash; no title/duration search.
        var response = await new global::Lyricify.Lyrics.Providers.Web.Kugou.Api()
            .GetSearchLyrics(hash: id.Value).WaitAsync(RequestTimeout, token);
        var candidate = response?.Candidates?.FirstOrDefault();
        if (candidate is null) return null;
        var raw = await Helper.GetLyricsAsync(candidate.Id, candidate.AccessKey).WaitAsync(RequestTimeout, token);
        return string.IsNullOrWhiteSpace(raw) ? null : Parse(raw, LyricsRawTypes.Krc, id);
    }

    private static async Task<LemonLyrics?> FetchAppleAsync(ExternalMusicId id, CancellationToken token)
    {
        var response = await new global::Lyricify.Lyrics.Providers.Web.AppleMusic.Api()
            .GetLyrics(id.Value).WaitAsync(RequestTimeout, token);
        return string.IsNullOrWhiteSpace(response?.Ttml) ? null : Parse(response.Ttml, LyricsRawTypes.Ttml, id);
    }

    private static async Task<LemonLyrics?> FetchSpotifyAsync(ExternalMusicId id, CancellationToken token)
    {
        var raw = await new global::Lyricify.Lyrics.Providers.Web.Spotify.Api()
            .GetLyrics(id.Value).WaitAsync(RequestTimeout, token);
        return string.IsNullOrWhiteSpace(raw) ? null : Parse(raw, LyricsRawTypes.Spotify, id);
    }

    private static async Task<LemonLyrics?> FetchMusixmatchAsync(ExternalMusicId id, CancellationToken token)
    {
        var raw = await new global::Lyricify.Lyrics.Providers.Web.Musixmatch.Api()
            .GetFullLyricsRaw(id.Value).WaitAsync(RequestTimeout, token);
        return string.IsNullOrWhiteSpace(raw) ? null : Parse(raw, LyricsRawTypes.Musixmatch, id);
    }

    private static async Task<LemonLyrics?> FetchSodaAsync(ExternalMusicId id, CancellationToken token)
    {
        var detail = await new global::Lyricify.Lyrics.Providers.Web.SodaMusic.Api()
            .GetDetail(id.Value).WaitAsync(RequestTimeout, token);
        var source = detail?.Lyric;
        if (string.IsNullOrWhiteSpace(source?.Content)) return null;
        var type = Enum.TryParse<LyricsRawTypes>(source.Type, true, out var declared)
            ? declared : TypeHelper.GetLyricsTypes(source.Content);
        var translation = source.Translations?.ChineseTranslation ?? source.LangTranslations?.Values
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value.Content))?.Content;
        return Parse(source.Content, type, id, translation) with
        {
            Language = string.IsNullOrWhiteSpace(source.Lang) ? null : source.Lang
        };
    }

    private static async Task<LemonLyrics?> FetchLrcLibAsync(ExternalMusicId id, CancellationToken token)
    {
        if (!int.TryParse(id.Value, out var number)) return null;
        var result = await new global::Lyricify.Lyrics.Providers.Web.LRCLIB.Api()
            .GetById(number).WaitAsync(RequestTimeout, token);
        if (!string.IsNullOrWhiteSpace(result?.SyncedLyrics)) return Parse(result.SyncedLyrics, LyricsRawTypes.Lrc, id);
        return string.IsNullOrWhiteSpace(result?.PlainLyrics) ? null : new LemonLyrics(result.PlainLyrics,
            Lines: result.PlainLyrics.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => new LemonLyricsLine(line)).ToArray(), Source: id.Namespace, SourceId: id);
    }

    private static LemonLyrics Parse(string raw, LyricsRawTypes type, ExternalMusicId sourceId,
        string? translation = null, string? romanization = null) =>
        LyricsConverter.Parse(raw, type, sourceId.Namespace, sourceId, translation, romanization);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    public IReadOnlyCollection<string> SearchNamespaces => MetadataNamespaces;

    public async Task<SearchResult> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Limit <= 0 || string.IsNullOrWhiteSpace(request.Keyword)) return new SearchResult(Id, []);

        var sourceNamespace = request.SourceNamespace?.Trim();
        var catalogSources = string.IsNullOrWhiteSpace(sourceNamespace)
            ? Sources
            : Sources
                .Where(source => string.Equals(
                    source.Namespace,
                    sourceNamespace,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
        if (catalogSources.Length == 0) return new SearchResult(Id, []);

        var limitPerSource = Math.Max(1, (int)Math.Ceiling(request.Limit / (double)catalogSources.Length));
        var searches = catalogSources.Select(source =>
            SearchCatalogSourceAsync(source, request.Keyword.Trim(), limitPerSource, cancellationToken));
        var sourceResults = await Task.WhenAll(searches);
        foreach (var failed in sourceResults.Where(result => result.Error is not null))
            _logger?.LogWarning(
                "Lyricify catalog search failed for {Source}: {Error}",
                failed.Namespace,
                failed.Error);

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
        if (parts.Length != 3) return null;

        var sourceNamespace = parts[0];
        var kind = parts[1];
        var value = Uri.UnescapeDataString(parts[2]);
        var result = sourceNamespace.ToLowerInvariant() switch
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

    private static void ConfigureOptionalCredentials()
    {
        var spotifySpDc = Environment.GetEnvironmentVariable("LEMONNEO_SPOTIFY_SP_DC");
        if (!string.IsNullOrWhiteSpace(spotifySpDc)) new Api().SetSpDc(spotifySpDc);

        var appleMediaUserToken = Environment.GetEnvironmentVariable("LEMONNEO_APPLE_MUSIC_USER_TOKEN");
        if (!string.IsNullOrWhiteSpace(appleMediaUserToken))
            new global::Lyricify.Lyrics.Providers.Web.AppleMusic.Api()
                .SetMediaUserToken(appleMediaUserToken);

        var musixmatchUserToken = Environment.GetEnvironmentVariable("LEMONNEO_MUSIXMATCH_USER_TOKEN");
        if (!string.IsNullOrWhiteSpace(musixmatchUserToken))
            new global::Lyricify.Lyrics.Providers.Web.Musixmatch.Api()
                .SetUserToken(musixmatchUserToken);
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
        if (!id.All(char.IsAsciiDigit)) return null;

        var client = _httpClient ?? throw new InvalidOperationException("The Lyricify provider is not initialized.");
        var requestValue = Uri.EscapeDataString($"[{{\"id\":{id}}}]");
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://music.163.com/api/v3/song/detail?c={requestValue}");
        message.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        message.Headers.Referrer = new Uri("https://music.163.com/");
        using var response =
            await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("songs", out var songs) ||
            songs.ValueKind != JsonValueKind.Array ||
            songs.GetArrayLength() == 0)
            return null;

        var song = songs[0];
        var title = ReadJsonString(song, "name");
        if (string.IsNullOrWhiteSpace(title)) return null;

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
        return new NeteaseSearchResult(title, artists, album, null, duration, id)
        {
            ArtworkUrl = albumValue.ValueKind == JsonValueKind.Object
                ? ReadJsonString(albumValue, "picUrl")
                : null
        };
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
        using var response =
            await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
            return null;

        var title = ReadJsonString(data, "song_name");
        if (string.IsNullOrWhiteSpace(title)) return null;

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
            hash)
        {
            ArtworkUrl = ArtworkHelper.WithSize(ReadJsonString(data, "img"))
        };
    }

    private static string? ReadJsonString(JsonElement value, string property)
    {
        return value.TryGetProperty(property, out var child) && child.ValueKind != JsonValueKind.Null
            ? child.ValueKind == JsonValueKind.String ? child.GetString() : child.ToString()
            : null;
    }

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
                    return new CatalogSearchResult(
                        source.Namespace,
                        results.Take(limit).ToArray(),
                        null);

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

            if (attempt < attempts) await Task.Delay(CatalogSearchRetryDelay, cancellationToken);
        }

        return new CatalogSearchResult(
            source.Namespace,
            [],
            $"Search failed after {attempts} attempt(s): {lastError}");
    }

    internal static Track ToSearchTrack(string sourceNamespace, ISearchResult result)
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
            ArtworkUri = GetArtworkUri(result),
            ExternalIds = identifiers
        };
    }

    private static Uri? GetArtworkUri(ISearchResult result)
    {
        return ArtworkHelper.NormalizeUrl(result.ArtworkUrl) is { } url ? new Uri(url) : null;
    }

    private static async Task<SourceSearchResult> SearchSourceAsync(
        SourceDefinition source,
        MetadataLookupRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var matches = await SearchMetadataMatchesAsync(source.Searcher.GetSearcher(), source.Namespace, request,
                cancellationToken);
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
            ? await searcher.SearchForResults(request.SearchKeyword!.Trim())
                .WaitAsync(RequestTimeout, cancellationToken) ?? []
            : await searcher.SearchForResults(helperTrack).WaitAsync(RequestTimeout, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return results.Select(result => ToMetadataMatch(sourceNamespace, result,
                customKeyword ? CompareHelper.CompareTrack(helperTrack, result) : null))
            .Where(match => match.Confidence >= request.MinimumConfidence)
            .OrderByDescending(match => match.Confidence)
            .Take(Math.Max(0, request.LimitPerSource)).ToArray();
    }

    private static MetadataMatch ToMetadataMatch(string sourceNamespace, ISearchResult result,
        CompareHelper.MatchType? matchType = null)
    {
        var level = matchType ?? result.MatchType ?? CompareHelper.MatchType.NoMatch;
        return new MetadataMatch
        {
            MetadataProviderId = "lyricify",
            SourceNamespace = sourceNamespace,
            Confidence = Confidence(level),
            MatchLevel = level.ToString(),
            Track = ToSearchTrack(sourceNamespace, result)
        };
    }

    private static IReadOnlyList<ExternalMusicId> GetIdentifiers(string sourceNamespace, ISearchResult result)
    {
        return result switch
        {
            QQMusicSearchResult qq =>
            [
                new ExternalMusicId(sourceNamespace, qq.Id, "song-id"),
                new ExternalMusicId(sourceNamespace, qq.Mid, "song-mid")
            ],
            NeteaseSearchResult netease => [new ExternalMusicId(sourceNamespace, netease.Id, "song-id")],
            KugouSearchResult kugou => [new ExternalMusicId(sourceNamespace, kugou.Hash, "audio-hash")],
            AppleMusicSearchResult apple => [new ExternalMusicId(sourceNamespace, apple.Id, "catalog-song-id")],
            SpotifySearchResult spotify => [new ExternalMusicId(sourceNamespace, spotify.Id, "track-id")],
            MusixmatchSearchResult musixmatch => CreateMusixmatchIds(sourceNamespace, musixmatch),
            SodaMusicSearchResult soda => [new ExternalMusicId(sourceNamespace, soda.Id, "track-id")],
            LRCLIBSearchResult lrcLib => [new ExternalMusicId(sourceNamespace, lrcLib.Id.ToString(), "lyrics-id")],
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
        if (!string.IsNullOrWhiteSpace(result.Isrc)) ids.Add(new ExternalMusicId(MusicIdNamespaces.Isrc, result.Isrc));

        return ids;
    }

    private static TrackMultiArtistMetadata ToHelperMetadata(Track track)
    {
        return new TrackMultiArtistMetadata
        {
            Title = track.Title,
            Artists = track.Artists.Select(artist => artist.Name).ToList(),
            Album = track.Album?.Title,
            DurationMs = track.Duration is null ? null : (int)track.Duration.Value.TotalMilliseconds,
            Isrc = track.ExternalIds.FirstOrDefault(id =>
                string.Equals(id.Namespace, MusicIdNamespaces.Isrc, StringComparison.OrdinalIgnoreCase))?.Value
        };
    }

    private static double Confidence(CompareHelper.MatchType? matchType)
    {
        return Math.Clamp(((int)(matchType ?? CompareHelper.MatchType.NoMatch)) / 100d, 0, 1);
    }

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
