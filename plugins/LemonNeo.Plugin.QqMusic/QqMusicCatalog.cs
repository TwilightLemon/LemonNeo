using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Exceptions;
using LemonNeo.Core.Music;

namespace LemonNeo.Plugin.QqMusic;

public sealed partial class QqMusicUserPlugin
{
    public IReadOnlyCollection<string> SearchNamespaces { get; } = [MusicIdNamespaces.QQMusic];

    public async Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default) =>
        new(ProviderId, await Required(_api).SearchTracksAsync(request, cancellationToken));

    public async Task<IReadOnlyList<Album>> SearchAlbumsAsync(SearchRequest request, CancellationToken cancellationToken = default) =>
        await Required(_api).SearchAlbumsAsync(request, await EnsureSessionAsync(cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<Artist>> SearchArtistsAsync(SearchRequest request, CancellationToken cancellationToken = default) =>
        await Required(_api).SearchArtistsAsync(request, await EnsureSessionAsync(cancellationToken), cancellationToken);

    public Task<AlbumDetail?> GetAlbumAsync(string albumId, CancellationToken cancellationToken = default) =>
        Required(_api).GetAlbumDetailAsync(albumId, cancellationToken);

    public Task<Artist?> GetArtistAsync(string artistId, CancellationToken cancellationToken = default) =>
        Required(_api).GetArtistAsync(artistId, cancellationToken);
}

internal sealed class QqSessionExpiredException() : LemonNeoException("The QQ Music session has expired.");

internal sealed partial class QqMusicApiClient
{
    internal static readonly Uri RpcUri = new("https://u.y.qq.com/cgi-bin/musicu.fcg");
    private static readonly JsonSerializerOptions RpcJson = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    public async Task<JsonElement> CallAsync(string module, string method, object parameters, CancellationToken ct,
        string? cookie = null, string userId = "0", int? loginType = null, CookieContainer? responseCookies = null,
        string responseKey = "req")
    {
        var comm = new Dictionary<string, object>
        {
            ["ct"] = 24,
            ["cv"] = 0,
            ["platform"] = "yqq",
            ["uin"] = userId,
            ["format"] = "json"
        };
        if (cookie is not null)
        {
            var cookies = cookie.Split(';', StringSplitOptions.TrimEntries);
            var key = cookies.FirstOrDefault(value => value.StartsWith("qqmusic_key=", StringComparison.Ordinal))
                      ?? cookies.FirstOrDefault(value => value.StartsWith("p_skey=", StringComparison.Ordinal));
            comm["g_tk"] = key is not null && key.IndexOf('=') < key.Length - 1
                ? QqMusicUserPlugin.CalculateGtk(key[(key.IndexOf('=') + 1)..]) : 5381;
        }
        if (loginType.HasValue)
        {
            comm["tmeAppID"] = "qqmusic";
            comm["tmeLoginType"] = loginType.Value;
        }
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["comm"] = comm,
            [responseKey] = new { module, method, param = parameters }
        }, RpcJson);
        using var request = cookie is null
            ? new HttpRequestMessage(HttpMethod.Get, RpcUri + "?format=json&data=" + Uri.EscapeDataString(payload))
            : new HttpRequestMessage(HttpMethod.Post, RpcUri)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/x-www-form-urlencoded")
            };
        request.Headers.Referrer = new Uri("https://y.qq.com/");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", "https://y.qq.com");
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new ProviderException($"QQ Music returned HTTP {(int)response.StatusCode}.");
        if (responseCookies is not null && response.Headers.TryGetValues("Set-Cookie", out var updates))
            foreach (var update in updates)
                responseCookies.SetCookies(RpcUri, update);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await ReadRpcJsonAsync(stream, ct);
        EnsureApiSuccess(document.RootElement, method);
        if (!TryGetObject(document.RootElement, out var result, responseKey))
            throw new ProviderException($"QQ Music returned no response for {method}.");
        EnsureApiSuccess(result, method);
        return TryGetObject(result, out var data, "data") ? data.Clone() : default;
    }

    private static async Task<JsonDocument> ReadRpcJsonAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new ProviderException("QQ Music returned an invalid JSON response.", exception);
        }
    }

    public async Task<IReadOnlyList<Track>> SearchTracksAsync(SearchRequest request, CancellationToken ct)
    {
        if (!MatchesNamespace(request)) return [];
        var result = await CallAsync("music.adaptor.SearchAdaptor", "do_search_v2", new
        {
            query = request.Keyword,
            search_type = 0,
            num_per_page = Math.Clamp(request.Limit, 1, 60),
            page_num = 1,
            grp = false,
            highlight = false
        }, ct);
        return SearchItems(result, "item_song", "items").Select(TryMapTrack).OfType<Track>().ToArray();
    }

    public async Task<IReadOnlyList<Album>> SearchAlbumsAsync(SearchRequest request, QqMusicCredentials? session, CancellationToken ct)
    {
        if (!MatchesNamespace(request)) return [];
        var albums = (await SearchCatalogAsync(request, 2, "album", session, ct))
            .Select(TryMapAlbum).OfType<Album>().ToArray();
        if (albums.Length > 0) return albums;
        return (await SearchTracksAsync(request, ct))
            .Where(track => track.Album is { Id: not null } album &&
                            album.Title.Contains(request.Keyword, StringComparison.OrdinalIgnoreCase))
            .Select(track => new Album { Id = track.Album!.Id!.Value, Title = track.Album.Title,
                ArtworkUri = track.ArtworkUri, Artists = track.Artists })
            .DistinctBy(album => album.Id).ToArray();
    }

    public async Task<IReadOnlyList<Artist>> SearchArtistsAsync(SearchRequest request, QqMusicCredentials? session, CancellationToken ct)
    {
        if (!MatchesNamespace(request)) return [];
        var artists = (await SearchCatalogAsync(request, 1, "singer", session, ct))
            .Select(TryMapArtist).OfType<Artist>().ToArray();
        if (artists.Length > 0) return artists;
        return (await SearchTracksAsync(request, ct)).SelectMany(track => track.Artists)
            .Where(artist => artist.Id is not null && artist.Name.Contains(request.Keyword, StringComparison.OrdinalIgnoreCase))
            .Select(artist => new Artist { Id = artist.Id!.Value, Name = artist.Name,
                ArtworkUri = SingerArtwork(artist.Id.Value.RemoteId) })
            .DistinctBy(artist => artist.Id).ToArray();
    }

    private async Task<IEnumerable<JsonElement>> SearchCatalogAsync(SearchRequest request, int type, string resultKey,
        QqMusicCredentials? session, CancellationToken ct)
    {
        const string module = "music.search.SearchCgiService";
        var result = await CallAsync(module, "DoSearchForQQMusicDesktop", new
        {
            query = request.Keyword,
            search_type = type,
            num_per_page = Math.Clamp(request.Limit, 1, 60),
            page_num = 1,
            remoteplace = "txt.yqq.center"
        }, ct, session?.Cookie, session?.UserId ?? "0", responseKey: module);
        return SearchItems(result, resultKey, "list");
    }

    private static IEnumerable<JsonElement> SearchItems(JsonElement result, string groupKey, string listKey) =>
        result.ValueKind == JsonValueKind.Object && TryGetObject(result, out var body, "body") &&
        TryGetObject(body, out var group, groupKey) && TryGetArray(group, out var list, listKey)
            ? list.EnumerateArray() : [];

    private static bool MatchesNamespace(SearchRequest request) => request.SourceNamespace is null ||
        request.SourceNamespace.Equals(MusicIdNamespaces.QQMusic, StringComparison.OrdinalIgnoreCase);

    public async Task<AlbumDetail?> GetAlbumDetailAsync(string albumMid, CancellationToken ct)
    {
        if (!IsValidMid(albumMid)) return null;
        var detail = await CallAsync("music.musichallAlbum.AlbumInfoServer", "GetAlbumDetail", new { albumMid }, ct);
        if (detail.ValueKind != JsonValueKind.Object || !TryGetObject(detail, out var info, "basicInfo") ||
            TryMapAlbum(info) is not { } album) return null;
        album = album with { Artists = ReadArtists(detail) };
        var tracks = new List<Track>();
        var seen = new HashSet<MusicItemId>();
        var offset = 0;
        const int pageSize = 100;
        while (true)
        {
            var page = await CallAsync("music.musichallAlbum.AlbumSongList", "GetAlbumSongList",
                new { albumMid, begin = offset, num = pageSize, order = 2 }, ct);
            if (page.ValueKind != JsonValueKind.Object || !TryGetArray(page, out var items, "songList"))
                throw new ProviderException("QQ Music returned no album tracks.");
            var previousCount = tracks.Count;
            foreach (var item in items.EnumerateArray())
                if (TryGetObject(item, out var song, "songInfo") && TryMapTrack(song) is { } track && seen.Add(track.Id))
                    tracks.Add(track);
            var count = items.GetArrayLength();
            offset += count;
            if (ReadInt32(page, "totalNum") is { } total ? offset >= total : count < pageSize) break;
            if (count == 0 || tracks.Count == previousCount)
                throw new ProviderException("QQ Music album pagination made no progress.");
        }
        return new AlbumDetail(album, tracks, ReadString(info, "desc"));
    }

    public async Task<Artist?> GetArtistAsync(string artistId, CancellationToken ct)
    {
        if (!IsValidMid(artistId)) return null;
        var data = await CallAsync("music.musichallSinger.SingerInfoInter", "GetSingerDetail",
            new { singer_mids = new[] { artistId }, pic = 1, group_singer = 0, wiki_singer = 0, ex_singer = 0 }, ct);
        if (data.ValueKind != JsonValueKind.Object || !TryGetArray(data, out var singers, "singer_list")) return null;
        foreach (var singer in singers.EnumerateArray())
            if (TryGetObject(singer, out var info, "basic_info") && TryMapArtist(info) is { } artist)
                return TryGetObject(singer, out var pictures, "pic")
                    ? artist with { ArtworkUri = ParseHttpsUri(ReadString(pictures, "pic")) ?? artist.ArtworkUri }
                    : artist;
        return null;
    }

    private static Album? TryMapAlbum(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            ReadFirstString(value, "albumMID", "albumMid", "albummid", "mid") is not { } id || !IsValidMid(id) ||
            ReadFirstString(value, "albumName", "albumname", "album_name", "name", "title") is not { } name) return null;
        return new Album
        {
            Id = new MusicItemId(ProviderId, id),
            Title = name,
            Artists = ReadArtists(value),
            ArtworkUri = ParseHttpsUri(ReadFirstString(value, "albumPic", "albumpic", "pic")) ??
                         new Uri($"https://y.gtimg.cn/music/photo_new/T002R500x500M000{id}.jpg")
        };
    }

    private static Artist? TryMapArtist(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            ReadFirstString(value, "singerMID", "singerMid", "singer_mid", "singermid", "mid") is not { } id ||
            !IsValidMid(id) || ReadFirstString(value, "singerName", "singer_name", "singername", "name", "title") is not { } name)
            return null;
        return new Artist
        {
            Id = new MusicItemId(ProviderId, id),
            Name = name,
            ArtworkUri = ParseHttpsUri(ReadFirstString(value, "singerPic", "singer_pic", "singerpic", "pic")) ??
                         SingerArtwork(id)
        };
    }

    private static IReadOnlyList<ArtistRef> ReadArtists(JsonElement value)
    {
        if (TryGetArray(value, out var singers, "singer") || TryGetArray(value, out singers, "singer_list"))
            return singers.EnumerateArray().Select(TryMapArtist).OfType<Artist>()
                .Select(artist => new ArtistRef { Id = artist.Id, Name = artist.Name }).ToArray();
        return ReadFirstString(value, "singerName", "singername", "singer_name") is { } name
            ? [new ArtistRef { Name = name }] : [];
    }

    private static bool IsValidMid(string value) => value.Length is > 0 and <= 128 && value.All(char.IsAsciiLetterOrDigit);

    private static Uri SingerArtwork(string mid) => new($"https://y.gtimg.cn/music/photo_new/T001R500x500M000{mid}.jpg");
}
