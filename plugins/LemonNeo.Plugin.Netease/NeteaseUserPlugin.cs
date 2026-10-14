using System.Globalization;
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

public sealed class NeteaseUserPlugin :
    ILemonPlugin,
    IMusicProvider,
    ITrackLookupProvider,
    IPlaylistProvider,
    IAuthenticationProvider
{
    private const string ProviderId = "netease";
    private const string CookieKey = "music-163-cookie";
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
        return Task.FromResult<IReadOnlyList<AuthMethod>>([AuthMethod.Cookie]);
    }

    public async Task<AuthenticationState> AuthenticateAsync(
        AuthenticationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Method != AuthMethod.Cookie ||
            !request.Parameters.TryGetValue("cookie", out var rawCookie))
            throw new ProviderException("Netease login requires a complete music.163.com cookie string.");

        var cookie = NormalizeCookie(rawCookie);
        var account = await GetAccountAsync(cookie, cancellationToken);
        var credentials = Required(_credentials);
        var configuration = Required(_settings);
        await credentials.SetSecretAsync(CookieKey, cookie, cancellationToken);
        configuration.Apply<AccountSettings>(_ =>
            new AccountSettings(account.UserId, account.Nickname, account.AvatarUri?.AbsoluteUri));
        _cookie = cookie;
        State = new AuthenticationState(true, account.Nickname, account.UserId, account.AvatarUri);
        _logger?.LogInformation("Authenticated Netease user {UserId}.", account.UserId);
        return State;
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var credentials = Required(_credentials);
        var configuration = Required(_settings);
        await credentials.DeleteSecretAsync(CookieKey, cancellationToken);
        configuration.Apply<AccountSettings>(_ => new AccountSettings());
        _cookie = null;
        State = new AuthenticationState(false);
    }

    public PluginDescriptor Descriptor { get; } = new(
        "LemonNeo.Plugin.Netease",
        "Netease User Library",
        "1.0.0");

    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _httpClient = context.HttpClientFactory.CreateClient(Descriptor.Id);
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
        if (!string.IsNullOrWhiteSpace(_cookie) && !string.IsNullOrWhiteSpace(userId))
            State = new AuthenticationState(true, nickname ?? userId, userId, TryCreateAvatarUri(avatar));

        context.RegisterProvider(this);
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cookie = null;
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
        var (cookie, userId) = RequiredAuthentication();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = await GetJsonAsync(
                $"api/user/playlist/?offset={offset}&limit={PlaylistPageSize}&uid={Uri.EscapeDataString(userId)}",
                cookie, cancellationToken);
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
                    ReadInt32(playlist, "trackCount"));
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

        var (cookie, _) = RequiredAuthentication();
        using var document = await GetJsonAsync(
            $"api/v6/playlist/detail?id={playlistId}&n={TrackBatchSize}",
            cookie,
            cancellationToken);
        EnsureApiSuccess(document.RootElement);
        if (!document.RootElement.TryGetProperty("playlist", out var playlist) ||
            playlist.ValueKind != JsonValueKind.Object)
            return null;

        var tracks = new List<Track>();
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
                using var details = await GetJsonAsync($"api/v3/song/detail?c={requestValue}", cookie, cancellationToken);
                EnsureApiSuccess(details.RootElement);
                if (!details.RootElement.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
                    throw new ProviderException("Netease returned no song collection.");
                foreach (var song in songs.EnumerateArray())
                    if (TryMapTrack(song) is { } track) byId[track.Id.RemoteId] = track;
            }

            // Removed or unavailable songs may legitimately be absent from song/detail.
            tracks = ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }
        else if (ReadInt32(playlist, "trackCount") is { } total && total > tracks.Count)
        {
            throw new ProviderException("Netease returned an incomplete playlist without track ids. Please retry the import.");
        }

        return new RemotePlaylist
        {
            Id = ReadString(playlist, "id") ?? playlistId,
            ProviderId = ProviderId,
            Name = ReadString(playlist, "name") ?? playlistId,
            Description = ReadString(playlist, "description"),
            Tracks = tracks
        };
    }

    public async Task<Track?> GetTrackAsync(
        string trackId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackId) || !trackId.All(char.IsAsciiDigit)) return null;

        var requestValue = Uri.EscapeDataString($"[{{\"id\":{trackId}}}]");
        using var document = await GetJsonAsync(
            $"api/v3/song/detail?c={requestValue}",
            _cookie ?? string.Empty,
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
        using var document = await GetJsonAsync("api/nuser/account/get", cookie, cancellationToken);
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

    private async Task<JsonDocument> GetJsonAsync(
        string relativePath,
        string cookie,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(new Uri("https://music.163.com/"), relativePath));
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.Referrer = new Uri("https://music.163.com/");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var response = await Required(_httpClient).SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ProviderException(
                $"Netease request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");

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

        var artists = new List<ArtistRef>();
        if (value.TryGetProperty("ar", out var artistValues) && artistValues.ValueKind == JsonValueKind.Array)
            foreach (var artist in artistValues.EnumerateArray())
                if (ReadString(artist, "name") is { Length: > 0 } name)
                    artists.Add(new ArtistRef { Name = name });

        AlbumRef? album = null;
        Uri? artwork = null;
        if (value.TryGetProperty("al", out var albumValue) && albumValue.ValueKind == JsonValueKind.Object)
        {
            if (ReadString(albumValue, "name") is { Length: > 0 } title) album = new AlbumRef { Title = title };

            if (Uri.TryCreate(ReadString(albumValue, "picUrl"), UriKind.Absolute, out var parsedArtwork))
                artwork = parsedArtwork;
        }

        var durationMilliseconds = ReadInt64(value, "dt");
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

    private (string Cookie, string UserId) RequiredAuthentication()
    {
        if (!State.IsAuthenticated || string.IsNullOrWhiteSpace(_cookie) ||
            string.IsNullOrWhiteSpace(State.UserId))
            throw new ProviderException("Netease is not authenticated. Run 'login netease' first.");

        return (_cookie, State.UserId);
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
