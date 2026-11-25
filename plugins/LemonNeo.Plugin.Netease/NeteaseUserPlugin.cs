using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LemonNeo.Abstractions.Plugins;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Abstractions.Storage;
using LemonNeo.Core.Exceptions;
using LemonNeo.Core.Music;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Plugin.Netease;

public sealed record AccountSettings(string? UserId = null, string? Nickname = null, string? AvatarUri = null);

public sealed partial class NeteaseUserPlugin :
    ILemonPlugin,
    IMusicProvider,
    ITrackLookupProvider,
    IPlaylistProvider,
    IWebAuthenticationProvider,
    ISearchProvider,
    ISearchNamespaceProvider,
    ICatalogProvider
{
    private const string ProviderId = "netease";
    private const string CookieKey = "music-163-cookie";
    private static readonly Uri Site = new("https://music.163.com/");
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private DateTimeOffset _nextRefresh;
    // Request sizes, not total import limits. Keep song-detail URLs below common proxy limits.
    private const int PlaylistPageSize = 100;
    private const int TrackBatchSize = 100;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36";

    private string? _cookie;
    private ICredentialVault? _credentials;

    private HttpClient? _httpClient;
    private ILogger<NeteaseUserPlugin>? _logger;
    private ISettingsScope? _settings;

    public AuthenticationState State { get; private set; } = new(false);

    public Task<IReadOnlyList<AuthMethod>> GetAvailableMethodsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<AuthMethod>>([AuthMethod.Browser, AuthMethod.Cookie]);
    }

    public async Task<AuthenticationState> AuthenticateAsync(
        AuthenticationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Method != AuthMethod.Cookie ||
            !request.Parameters.TryGetValue("cookie", out var rawCookie))
            throw new ProviderException("Netease login requires a complete music.163.com cookie string.");

        var cookie = NormalizeCookie(rawCookie);
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            var account = await GetAccountAsync(cookie, cancellationToken);
            await Required(_credentials).SetSecretAsync(CookieKey, cookie, cancellationToken);
            Required(_settings).Apply<AccountSettings>(_ =>
                new AccountSettings(account.UserId, account.Nickname, account.AvatarUri?.AbsoluteUri));
            _cookie = cookie;
            _nextRefresh = DateTimeOffset.UtcNow.AddHours(12);
            State = new AuthenticationState(true, account.Nickname, account.UserId, account.AvatarUri);
            _logger?.LogInformation("Authenticated Netease user {UserId}.", account.UserId);
            return State;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            await Required(_credentials).DeleteSecretAsync(CookieKey, cancellationToken);
            Required(_settings).Apply<AccountSettings>(_ => new AccountSettings());
            _cookie = null;
            State = new AuthenticationState(false);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public PluginDescriptor Descriptor { get; } = new(
        "LemonNeo.Plugin.Netease",
        "Netease User Library",
        "1.0.0");

    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _httpClient = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _settings = context.Settings;
        await _settings.RegisterAsync(nameof(AccountSettings), new AccountSettings(),
            cancellationToken: cancellationToken);
        _credentials = context.Credentials;
        _logger = context.LoggerFactory.CreateLogger<NeteaseUserPlugin>();
        _cookie = await _credentials.GetSecretAsync(CookieKey, cancellationToken);
        var account = _settings.Get<AccountSettings>();
        var userId = account.UserId;
        var nickname = account.Nickname;
        var avatar = account.AvatarUri;
        if (!string.IsNullOrWhiteSpace(_cookie))
        {
            if (!string.IsNullOrWhiteSpace(userId))
                State = new AuthenticationState(true, nickname ?? userId, userId, TryCreateAvatarUri(avatar));
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                await GetCookieAsync(startup.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _nextRefresh = default;
            }
        }

        context.RegisterProvider(this);
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cookie = null;
        _httpClient?.Dispose();
        _httpClient = null;
        _settings = null;
        _credentials = null;
        _logger = null;
        State = new AuthenticationState(false);
        return ValueTask.CompletedTask;
    }

    public string Id => ProviderId;

    ProviderDescriptor IMusicProvider.Descriptor { get; } = new(
        "Netease Music",
        "Imports the authenticated user's Netease Music playlists.");

    public async IAsyncEnumerable<RemotePlaylistSummary> GetUserPlaylistsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (_, userId) = await RequiredAuthenticationAsync(cancellationToken);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = await GetAuthenticatedJsonAsync(
                $"api/user/playlist/?offset={offset}&limit={PlaylistPageSize}&uid={Uri.EscapeDataString(userId)}",
                cancellationToken);
            EnsureApiSuccess(document.RootElement);
            if (!document.RootElement.TryGetProperty("playlist", out var playlists) ||
                playlists.ValueKind != JsonValueKind.Array)
                throw new ProviderException("Netease returned no playlist collection.");

            var previousCount = seen.Count;
            foreach (var playlist in playlists.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryReadId(playlist, "id", out var id) || !seen.Add(id)) continue;
                yield return new RemotePlaylistSummary(id, ReadString(playlist, "name") ?? id,
                    ReadInt32(playlist, "trackCount"))
                {
                    ArtworkUri = TryCreateAvatarUri(ReadString(playlist, "coverImgUrl"))
                };
            }

            var count = playlists.GetArrayLength();
            var hasMore = document.RootElement.TryGetProperty("more", out var more)
                && more.ValueKind is JsonValueKind.True or JsonValueKind.False ? more.GetBoolean() : (bool?)null;
            if (hasMore == false || (hasMore is null && count < PlaylistPageSize)) yield break;
            if (count == 0 || seen.Count == previousCount)
                throw new ProviderException("Netease playlist pagination made no progress. Please retry the import.");
            offset = checked(offset + count);
        }
    }

    public async Task<RemotePlaylist?> GetPlaylistAsync(
        string playlistId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId) || !playlistId.All(char.IsAsciiDigit))
            throw new ProviderException("A Netease playlist id must contain only digits.");

        using var document = await GetAuthenticatedJsonAsync(
            $"api/v6/playlist/detail?id={playlistId}&n={TrackBatchSize}",
            cancellationToken);
        EnsureApiSuccess(document.RootElement);
        if (!document.RootElement.TryGetProperty("playlist", out var playlist) ||
            playlist.ValueKind != JsonValueKind.Object)
            return null;

        var tracks = new List<Track>();
        var isComplete = false;
        if (playlist.TryGetProperty("tracks", out var values) && values.ValueKind == JsonValueKind.Array)
            foreach (var value in values.EnumerateArray())
                if (TryMapTrack(value) is { } track)
                    tracks.Add(track);

        // The detail endpoint can truncate tracks even for authenticated users. trackIds is the
        // authoritative ordered collection; resolve missing details in batches and preserve its order.
        if (playlist.TryGetProperty("trackIds", out var trackIds) && trackIds.ValueKind == JsonValueKind.Array)
        {
            var ids = trackIds.EnumerateArray()
                .Select(value => TryReadId(value, "id", out var id) ? id : null)
                .OfType<string>().ToArray();
            var byId = tracks.DistinctBy(track => track.Id.RemoteId)
                .ToDictionary(track => track.Id.RemoteId, StringComparer.Ordinal);
            foreach (var batch in ids.Where(id => !byId.ContainsKey(id)).Distinct().Chunk(TrackBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requestValue = Uri.EscapeDataString("[" + string.Join(",", batch.Select(id => $"{{\"id\":{id}}}")) + "]");
                using var details = await GetAuthenticatedJsonAsync($"api/v3/song/detail?c={requestValue}", cancellationToken);
                EnsureApiSuccess(details.RootElement);
                if (!details.RootElement.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
                    throw new ProviderException("Netease returned no song collection.");
                foreach (var song in songs.EnumerateArray())
                    if (TryMapTrack(song) is { } track) byId[track.Id.RemoteId] = track;
            }

            // Removed or unavailable songs may legitimately be absent from song/detail.
            tracks = ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            var trackCount = ReadInt32(playlist, "trackCount");
            isComplete = ids.Length == trackIds.GetArrayLength() && tracks.Count == ids.Length &&
                         (trackCount is null || trackCount == ids.Length);
        }
        else if (ReadInt32(playlist, "trackCount") is { } total && total > tracks.Count)
        {
            throw new ProviderException("Netease returned an incomplete playlist without track ids. Please retry the import.");
        }
        else
        {
            isComplete = ReadInt32(playlist, "trackCount") is { } count && count == tracks.Count &&
                         playlist.TryGetProperty("tracks", out var allTracks) &&
                         allTracks.ValueKind == JsonValueKind.Array && allTracks.GetArrayLength() == tracks.Count;
        }

        return new RemotePlaylist
        {
            Id = ReadString(playlist, "id") ?? playlistId,
            ProviderId = ProviderId,
            Name = ReadString(playlist, "name") ?? playlistId,
            Description = ReadString(playlist, "description"),
            ArtworkUri = TryCreateAvatarUri(ReadString(playlist, "coverImgUrl")),
            IsComplete = isComplete,
            Tracks = tracks
        };
    }

    public async Task<Track?> GetTrackAsync(
        string trackId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackId) || !trackId.All(char.IsAsciiDigit)) return null;

        var requestValue = Uri.EscapeDataString($"[{{\"id\":{trackId}}}]");
        using var document = await RequestJsonAsync(
            $"api/v3/song/detail?c={requestValue}",
            await GetCookieAsync(cancellationToken) ?? "",
            cancellationToken);
        EnsureApiSuccess(document.RootElement);
        if (!document.RootElement.TryGetProperty("songs", out var songs) ||
            songs.ValueKind != JsonValueKind.Array ||
            songs.GetArrayLength() == 0)
            return null;

        return TryMapTrack(songs[0]);
    }

    private async Task<(string UserId, string Nickname, Uri? AvatarUri)> GetAccountAsync(
        string cookie,
        CancellationToken cancellationToken)
    {
        using var document = await RequestJsonAsync("api/nuser/account/get", cookie, cancellationToken);
        EnsureApiSuccess(document.RootElement);
        if (!document.RootElement.TryGetProperty("profile", out var profile) ||
            profile.ValueKind != JsonValueKind.Object ||
            !TryReadId(profile, "userId", out var userId))
            throw new ProviderException(
                "The cookie is not associated with an authenticated Netease Music profile.");

        return (userId, ReadString(profile, "nickname") ?? userId,
            TryCreateAvatarUri(ReadString(profile, "avatarUrl")));
    }

    private static Uri? TryCreateAvatarUri(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
    }

    private async Task<JsonDocument> RequestJsonAsync(
        string relativePath,
        string cookie,
        CancellationToken cancellationToken,
        CookieContainer? responseCookies = null,
        HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(
            method ?? HttpMethod.Get,
            new Uri(Site, relativePath));
        if (method == HttpMethod.Post) request.Content = new FormUrlEncodedContent([]);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.Referrer = Site;
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var response = await Required(_httpClient).SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ProviderException(
                $"Netease request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");

        if (responseCookies is not null && response.Headers.TryGetValues("Set-Cookie", out var updates))
            foreach (var update in updates)
            {
                try { responseCookies.SetCookies(request.RequestUri!, update); }
                catch (CookieException) { }
            }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new ProviderException("Netease returned an invalid JSON response.", exception);
        }
    }

    private static Track? TryMapTrack(JsonElement value)
    {
        if (!TryReadId(value, "id", out var id) || string.IsNullOrWhiteSpace(ReadString(value, "name"))) return null;

        var artists = MapArtists(value);
        AlbumRef? album = null;
        Uri? artwork = null;
        if ((value.TryGetProperty("al", out var albumValue) || value.TryGetProperty("album", out albumValue)) &&
            albumValue.ValueKind == JsonValueKind.Object)
        {
            if (ReadString(albumValue, "name") is { Length: > 0 } title)
                album = new AlbumRef { Title = title, Id = ReadMusicId(albumValue) };

            if (Uri.TryCreate(ReadString(albumValue, "picUrl"), UriKind.Absolute, out var parsedArtwork))
                artwork = parsedArtwork;
        }

        var durationMilliseconds = ReadInt64(value, "dt") ?? ReadInt64(value, "duration");
        return new Track
        {
            Id = new MusicItemId(ProviderId, id),
            Title = ReadString(value, "name")!,
            Artists = artists,
            Album = album,
            Duration = durationMilliseconds is > 0
                ? TimeSpan.FromMilliseconds(durationMilliseconds.Value)
                : null,
            ArtworkUri = artwork,
            ExternalIds =
            [
                new ExternalMusicId(MusicIdNamespaces.NeteaseMusic, id, "song-id")
            ]
        };
    }

    private static string NormalizeCookie(string value)
    {
        var cookie = value.Trim();
        if (cookie.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
            cookie = cookie["Cookie:".Length..].Trim();

        if (cookie.Length == 0 || cookie.Length > 65536 || cookie.IndexOfAny(['\r', '\n']) >= 0 ||
            !cookie.Contains('='))
            throw new ProviderException("The Netease cookie is empty or malformed.");

        return cookie;
    }

    private static void EnsureApiSuccess(JsonElement root)
    {
        var code = ReadInt32(root, "code");
        if (code is not null and not 200) throw new ProviderException($"Netease API returned code {code}.");
    }

    private static string? ReadString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var child) || child.ValueKind == JsonValueKind.Null) return null;

        return child.ValueKind == JsonValueKind.String ? child.GetString() : child.ToString();
    }

    private static bool TryReadId(JsonElement value, string property, out string id)
    {
        id = ReadString(value, property) ?? string.Empty;
        return id.Length > 0 && id.All(char.IsAsciiDigit);
    }

    private static int? ReadInt32(JsonElement value, string property)
    {
        return int.TryParse(ReadString(value, property), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var result)
            ? result
            : null;
    }

    private static long? ReadInt64(JsonElement value, string property)
    {
        return long.TryParse(ReadString(value, property), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var result)
            ? result
            : null;
    }

    private static T Required<T>(T? value) where T : class
    {
        return value ?? throw new InvalidOperationException("The Netease plugin is not initialized.");
    }
}
