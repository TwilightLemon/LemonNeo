using System.Globalization;
using System.Text.Json;
using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Plugin.QqMusic;

internal sealed record QqMusicCredentials(string Cookie, string UserId, long Gtk);

internal sealed record QqMusicProfile(
    string UserId,
    string Nickname,
    Uri? AvatarUri,
    IReadOnlyList<RemotePlaylistSummary> CreatedPlaylists);

internal sealed class QqMusicApiClient(HttpClient httpClient)
{
    private const string ProviderId = "qqmusic";
    private const string IdAuthority = "y.qq.com/api";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36";

    public async Task<QqMusicProfile> GetProfileAsync(
        QqMusicCredentials authentication,
        CancellationToken cancellationToken)
    {
        var userId = Uri.EscapeDataString(authentication.UserId);
        using var document = await GetJsonAsync(
            $"https://c.y.qq.com/rsc/fcgi-bin/fcg_get_profile_homepage.fcg?" +
            $"loginUin={userId}&hostUin=0&format=json&inCharset=utf8&outCharset=utf-8&" +
            $"notice=0&platform=yqq&needNewCode=0&cid=205360838&ct=20&userid={userId}&" +
            "reqfrom=1&reqtype=0",
            authentication.Cookie,
            cancellationToken);
        EnsureApiSuccess(document.RootElement, "profile");
        if (!TryGetObject(document.RootElement, out var data, "data") ||
            !TryGetObject(data, out var creator, "creator") ||
            string.IsNullOrWhiteSpace(ReadString(creator, "nick")))
        {
            throw new ProviderException(
                "The cookie is not associated with an authenticated QQ Music profile.");
        }

        var created = new List<RemotePlaylistSummary>();
        if (TryGetArray(data, out var myMusic, "mymusic"))
        {
            foreach (var item in myMusic.EnumerateArray())
            {
                if (ReadString(item, "type") == "1" &&
                    TryReadIdentifier(item, out var id, "id"))
                {
                    created.Add(new RemotePlaylistSummary(
                        $"playlist:{id}",
                        ReadString(item, "title") ?? "我喜欢",
                        ReadTrackCount(item)));
                }
            }
        }

        if (TryGetObject(data, out var myDiss, "mydiss") &&
            TryGetArray(myDiss, out var playlists, "list"))
        {
            foreach (var item in playlists.EnumerateArray())
            {
                if (!TryReadIdentifier(item, out var id, "dissid"))
                {
                    continue;
                }

                created.Add(new RemotePlaylistSummary(
                    $"playlist:{id}",
                    ReadString(item, "title") ?? id,
                    ReadTrackCount(item)));
            }
        }

        var deduplicated = created
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        return new QqMusicProfile(
            authentication.UserId,
            ReadString(creator, "nick")!,
            ParseHttpsUri(ReadString(creator, "headpic")),
            deduplicated);
    }

    public async Task<IReadOnlyList<RemotePlaylistSummary>> GetFollowedPlaylistsAsync(
        QqMusicCredentials authentication,
        CancellationToken cancellationToken)
    {
        const int pageSize = 25;
        var result = new List<RemotePlaylistSummary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var start = 0; start < 1000; start += pageSize)
        {
            var userId = Uri.EscapeDataString(authentication.UserId);
            using var document = await GetJsonAsync(
                $"https://c.y.qq.com/fav/fcgi-bin/fcg_get_profile_order_asset.fcg?" +
                $"g_tk={authentication.Gtk}&loginUin={userId}&hostUin=0&format=json&" +
                $"inCharset=utf8&outCharset=utf-8&notice=0&platform=yqq&needNewCode=0&" +
                $"ct=20&cid=205360956&userid={userId}&reqtype=3&sin={start}&ein={start + pageSize - 1}",
                authentication.Cookie,
                cancellationToken);
            EnsureApiSuccess(document.RootElement, "followed playlists");
            if (!TryGetObject(document.RootElement, out var data, "data") ||
                !TryGetArray(data, out var playlists, "cdlist"))
            {
                break;
            }

            var pageCount = 0;
            var addedCount = 0;
            foreach (var item in playlists.EnumerateArray())
            {
                pageCount++;
                if (!TryReadIdentifier(item, out var id, "dissid") || !seen.Add(id))
                {
                    continue;
                }

                addedCount++;
                result.Add(new RemotePlaylistSummary(
                    $"playlist:{id}",
                    ReadString(item, "dissname") ?? id,
                    ReadTrackCount(item)));
            }

            if (pageCount < pageSize || addedCount == 0)
            {
                break;
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<RemotePlaylistSummary>> GetPurchasedAlbumsAsync(
        QqMusicCredentials authentication,
        CancellationToken cancellationToken)
    {
        const int pageSize = 50;
        var result = new List<RemotePlaylistSummary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var start = 0; start < 1000; start += pageSize)
        {
            using var document = await GetJsonAsync(
                $"https://c.y.qq.com/shop/fcgi-bin/fcg_get_order?from=1&cmd=sales_album&" +
                $"type=1&format=json&inCharset=utf-8&outCharset=utf-8&notice=0&platform=yqq&" +
                $"needNewCode=1&uin={Uri.EscapeDataString(authentication.UserId)}&" +
                $"g_tk={authentication.Gtk}&start={start}&num={pageSize}",
                authentication.Cookie,
                cancellationToken);
            EnsureApiSuccess(document.RootElement, "purchased albums");
            if (!TryGetObject(document.RootElement, out var data, "data") ||
                !TryGetArray(data, out var albums, "albumlist"))
            {
                break;
            }

            var pageCount = 0;
            var addedCount = 0;
            foreach (var item in albums.EnumerateArray())
            {
                pageCount++;
                if (!TryReadIdentifier(item, out var id, "albummid") || !seen.Add(id))
                {
                    continue;
                }

                addedCount++;
                result.Add(new RemotePlaylistSummary(
                    $"album:{id}",
                    ReadString(item, "album_name") ?? id,
                    ReadTrackCount(item)));
            }

            if (pageCount < pageSize || addedCount == 0)
            {
                break;
            }
        }

        return result;
    }

    public async Task<RemotePlaylist?> GetPlaylistAsync(
        QqMusicCredentials authentication,
        string playlistId,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            $"https://c.y.qq.com/qzone/fcg-bin/fcg_ucc_getcdinfo_byids_cp.fcg?" +
            $"type=1&json=1&utf8=1&onlysong=0&disstid={playlistId}&format=json&" +
            $"g_tk={authentication.Gtk}&loginUin={Uri.EscapeDataString(authentication.UserId)}&" +
            "hostUin=0&inCharset=utf8&outCharset=utf-8&notice=0&platform=yqq&needNewCode=0",
            authentication.Cookie,
            cancellationToken);
        EnsureApiSuccess(document.RootElement, "playlist");
        if (!TryGetArray(document.RootElement, out var playlists, "cdlist") ||
            playlists.GetArrayLength() == 0)
        {
            return null;
        }

        var playlist = playlists[0];
        if (ReadInt64(playlist, "code") is { } playlistCode && playlistCode != 0)
        {
            throw new ProviderException($"QQ Music playlist API returned code {playlistCode}.");
        }

        var tracks = MapTrackArray(playlist, "songlist");
        return new RemotePlaylist
        {
            Id = $"playlist:{playlistId}",
            ProviderId = ProviderId,
            Name = ReadString(playlist, "dissname") ?? playlistId,
            Description = ReadString(playlist, "desc"),
            Tracks = tracks
        };
    }

    public async Task<RemotePlaylist?> GetAlbumAsync(
        QqMusicCredentials authentication,
        string albumMid,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            $"https://c.y.qq.com/v8/fcg-bin/fcg_v8_album_info_cp.fcg?ct=24&" +
            $"albummid={Uri.EscapeDataString(albumMid)}&g_tk={authentication.Gtk}&" +
            $"loginUin={Uri.EscapeDataString(authentication.UserId)}&hostUin=0&format=json&" +
            "inCharset=utf8&outCharset=utf-8&notice=0&platform=yqq.json&needNewCode=0&" +
            "song_begin=0&song_num=999",
            authentication.Cookie,
            cancellationToken);
        EnsureApiSuccess(document.RootElement, "album");
        if (!TryGetObject(document.RootElement, out var album, "data"))
        {
            return null;
        }

        var tracks = MapTrackArray(album, "list");
        return new RemotePlaylist
        {
            Id = $"album:{albumMid}",
            ProviderId = ProviderId,
            Name = ReadString(album, "name") ?? albumMid,
            Description = ReadString(album, "desc"),
            Tracks = tracks
        };
    }

    private async Task<JsonDocument> GetJsonAsync(
        string uri,
        string cookie,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.Referrer = new Uri("https://y.qq.com/");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ProviderException(
                $"QQ Music request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new ProviderException("QQ Music returned an invalid JSON response.", exception);
        }
    }

    private static IReadOnlyList<Track> MapTrackArray(JsonElement parent, string property)
    {
        if (!TryGetArray(parent, out var values, property))
        {
            return [];
        }

        var result = new List<Track>();
        foreach (var value in values.EnumerateArray())
        {
            if (TryMapTrack(value) is { } track)
            {
                result.Add(track);
            }
        }

        return result;
    }

    private static Track? TryMapTrack(JsonElement value)
    {
        var songMid = ReadFirstString(value, "songmid", "mid");
        var songId = ReadFirstString(value, "songid", "id");
        var remoteId = !string.IsNullOrWhiteSpace(songMid) ? songMid : songId;
        var title = ReadFirstString(value, "songname", "name", "title");
        if (string.IsNullOrWhiteSpace(remoteId) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var artists = new List<ArtistRef>();
        if (TryGetArray(value, out var singerValues, "singer"))
        {
            foreach (var singer in singerValues.EnumerateArray())
            {
                if (ReadString(singer, "name") is not { Length: > 0 } name)
                {
                    continue;
                }

                var singerMid = ReadString(singer, "mid");
                artists.Add(new ArtistRef
                {
                    Name = name,
                    Id = string.IsNullOrWhiteSpace(singerMid)
                        ? null
                        : new MusicItemId(ProviderId, singerMid)
                });
            }
        }

        var albumMid = ReadFirstString(value, "albummid");
        var albumName = ReadFirstString(value, "albumname");
        if (TryGetObject(value, out var albumValue, "album"))
        {
            albumMid ??= ReadString(albumValue, "mid");
            albumName ??= ReadString(albumValue, "name");
        }

        var externalIds = new List<ExternalMusicId>();
        if (!string.IsNullOrWhiteSpace(songId))
        {
            externalIds.Add(new ExternalMusicId(
                MusicIdNamespaces.QQMusic,
                songId,
                IdAuthority,
                "song-id"));
        }

        if (!string.IsNullOrWhiteSpace(songMid))
        {
            externalIds.Add(new ExternalMusicId(
                MusicIdNamespaces.QQMusic,
                songMid,
                IdAuthority,
                "song-mid"));
        }

        var durationSeconds = ReadInt64(value, "interval");
        return new Track
        {
            Id = new MusicItemId(ProviderId, remoteId),
            Title = title,
            Artists = artists,
            Album = string.IsNullOrWhiteSpace(albumName)
                ? null
                : new AlbumRef
                {
                    Title = albumName,
                    Id = string.IsNullOrWhiteSpace(albumMid)
                        ? null
                        : new MusicItemId(ProviderId, albumMid)
                },
            Duration = durationSeconds is > 0 ? TimeSpan.FromSeconds(durationSeconds.Value) : null,
            ArtworkUri = string.IsNullOrWhiteSpace(albumMid)
                ? null
                : new Uri($"https://y.gtimg.cn/music/photo_new/T002R500x500M000{albumMid}.jpg"),
            ExternalIds = externalIds,
            IsPlayable = false
        };
    }

    private static void EnsureApiSuccess(JsonElement root, string operation)
    {
        if (ReadInt64(root, "code") is { } code && code != 0)
        {
            throw new ProviderException($"QQ Music {operation} API returned code {code}.");
        }
    }

    private static int? ReadTrackCount(JsonElement value) =>
        ReadInt32(value, "songnum") ??
        ReadInt32(value, "song_count") ??
        ReadInt32(value, "songcount") ??
        ReadInt32(value, "total_song_num");

    private static bool TryReadIdentifier(JsonElement value, out string id, params string[] properties)
    {
        id = ReadFirstString(value, properties) ?? string.Empty;
        return id.Length > 0;
    }

    private static string? ReadFirstString(JsonElement value, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (ReadString(value, property) is { Length: > 0 } result)
            {
                return result;
            }
        }

        return null;
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
        int.TryParse(ReadString(value, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static long? ReadInt64(JsonElement value, string property) =>
        long.TryParse(ReadString(value, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static bool TryGetObject(JsonElement value, out JsonElement result, string property)
    {
        if (value.TryGetProperty(property, out result) && result.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryGetArray(JsonElement value, out JsonElement result, string property)
    {
        if (value.TryGetProperty(property, out result) && result.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        result = default;
        return false;
    }

    private static Uri? ParseHttpsUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.StartsWith("//", StringComparison.Ordinal)
            ? $"https:{value}"
            : value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? $"https://{value["http://".Length..]}"
                : value;
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ? uri : null;
    }
}
