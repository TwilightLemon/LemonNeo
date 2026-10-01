using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Plugin.Netease;

public sealed class NeteaseUserPlugin :
    ILemonPlugin,
    IMusicProvider,
    IPlaylistProvider,
    IAuthenticationProvider
{
    private const string ProviderId = "netease";
    private const string CookieKey = "music-163-cookie";
    private const string UserIdKey = "user-id";
    private const string NicknameKey = "nickname";
    private const string IdAuthority = "music.163.com/api";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36";

    private HttpClient? _httpClient;
    private IPluginStorage? _storage;
    private ICredentialVault? _credentials;
    private ILogger<NeteaseUserPlugin>? _logger;
    private string? _cookie;
    private AuthenticationState _state = new(false);

    public PluginDescriptor Descriptor { get; } = new(
        "org.lemonneo.netease.user",
        "Netease User Library",
        "1.0.0");

    public string Id => ProviderId;

    ProviderDescriptor IMusicProvider.Descriptor { get; } = new(
        "Netease Music",
        "Imports the authenticated user's Netease Music playlists.");

    public AuthenticationState State => _state;

    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _httpClient = context.HttpClientFactory.CreateClient(Descriptor.Id);
        _storage = context.Storage;
        _credentials = context.Credentials;
        _logger = context.LoggerFactory.CreateLogger<NeteaseUserPlugin>();
        _cookie = await _credentials.GetSecretAsync(CookieKey, cancellationToken);
        var userId = await _storage.GetAsync(UserIdKey, cancellationToken);
        var nickname = await _storage.GetAsync(NicknameKey, cancellationToken);
        if (!string.IsNullOrWhiteSpace(_cookie) && !string.IsNullOrWhiteSpace(userId))
        {
            _state = new AuthenticationState(true, nickname ?? userId, userId);
        }

        context.RegisterProvider(this);
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cookie = null;
        _httpClient = null;
        _storage = null;
        _credentials = null;
        _logger = null;
        _state = new AuthenticationState(false);
        return ValueTask.CompletedTask;
    }

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
        {
            throw new ProviderException("Netease login requires a complete music.163.com cookie string.");
        }

        var cookie = NormalizeCookie(rawCookie);
        var account = await GetAccountAsync(cookie, cancellationToken);
        var credentials = Required(_credentials);
        var storage = Required(_storage);
        await credentials.SetSecretAsync(CookieKey, cookie, cancellationToken);
        await storage.SetAsync(UserIdKey, account.UserId, cancellationToken);
        await storage.SetAsync(NicknameKey, account.Nickname, cancellationToken);
        _cookie = cookie;
        _state = new AuthenticationState(true, account.Nickname, account.UserId);
        _logger?.LogInformation("Authenticated Netease user {UserId}.", account.UserId);
        return _state;
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var credentials = Required(_credentials);
        var storage = Required(_storage);
        await credentials.DeleteSecretAsync(CookieKey, cancellationToken);
        await storage.DeleteAsync(UserIdKey, cancellationToken);
        await storage.DeleteAsync(NicknameKey, cancellationToken);
        _cookie = null;
        _state = new AuthenticationState(false);
    }

    public async IAsyncEnumerable<RemotePlaylistSummary> GetUserPlaylistsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (cookie, userId) = RequiredAuthentication();
        using var document = await GetJsonAsync(
            $"api/user/playlist/?offset=0&limit=1000&uid={Uri.EscapeDataString(userId)}",
            cookie,
            cancellationToken);
        EnsureApiSuccess(document.RootElement);
        if (!document.RootElement.TryGetProperty("playlist", out var playlists) ||
            playlists.ValueKind != JsonValueKind.Array)
        {
            throw new ProviderException("Netease returned no playlist collection.");
        }

        foreach (var playlist in playlists.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadId(playlist, "id", out var id))
            {
                continue;
            }

            var name = ReadString(playlist, "name") ?? id;
            var count = ReadInt32(playlist, "trackCount");
            yield return new RemotePlaylistSummary(id, name, count);
        }
    }

    public async Task<RemotePlaylist?> GetPlaylistAsync(
        string playlistId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId) || !playlistId.All(char.IsAsciiDigit))
        {
            throw new ProviderException("A Netease playlist id must contain only digits.");
        }

        var (cookie, _) = RequiredAuthentication();
        using var document = await GetJsonAsync(
            $"api/v6/playlist/detail?id={playlistId}&offset=0&total=true&limit=100000&n=10000",
            cookie,
            cancellationToken);
        EnsureApiSuccess(document.RootElement);
        if (!document.RootElement.TryGetProperty("playlist", out var playlist) ||
            playlist.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var tracks = new List<Track>();
        if (playlist.TryGetProperty("tracks", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
            {
                if (TryMapTrack(value) is { } track)
                {
                    tracks.Add(track);
                }
            }
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

    private async Task<(string UserId, string Nickname)> GetAccountAsync(
        string cookie,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("api/nuser/account/get", cookie, cancellationToken);
        EnsureApiSuccess(document.RootElement);
        if (!document.RootElement.TryGetProperty("profile", out var profile) ||
            profile.ValueKind != JsonValueKind.Object ||
            !TryReadId(profile, "userId", out var userId))
        {
            throw new ProviderException(
                "The cookie is not associated with an authenticated Netease Music profile.");
        }

        return (userId, ReadString(profile, "nickname") ?? userId);
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
        {
            throw new ProviderException(
                $"Netease request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
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
        if (!TryReadId(value, "id", out var id) || string.IsNullOrWhiteSpace(ReadString(value, "name")))
        {
            return null;
        }

        var artists = new List<ArtistRef>();
        if (value.TryGetProperty("ar", out var artistValues) && artistValues.ValueKind == JsonValueKind.Array)
        {
            foreach (var artist in artistValues.EnumerateArray())
            {
                if (ReadString(artist, "name") is { Length: > 0 } name)
                {
                    artists.Add(new ArtistRef { Name = name });
                }
            }
        }

        AlbumRef? album = null;
        Uri? artwork = null;
        if (value.TryGetProperty("al", out var albumValue) && albumValue.ValueKind == JsonValueKind.Object)
        {
            if (ReadString(albumValue, "name") is { Length: > 0 } title)
            {
                album = new AlbumRef { Title = title };
            }

            if (Uri.TryCreate(ReadString(albumValue, "picUrl"), UriKind.Absolute, out var parsedArtwork))
            {
                artwork = parsedArtwork;
            }
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
                new ExternalMusicId(MusicIdNamespaces.NeteaseMusic, id, IdAuthority, "song-id")
            ],
            IsPlayable = false
        };
    }

    private static string NormalizeCookie(string value)
    {
        var cookie = value.Trim();
        if (cookie.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
        {
            cookie = cookie["Cookie:".Length..].Trim();
        }

        if (cookie.Length == 0 || cookie.Length > 65536 || cookie.IndexOfAny(['\r', '\n']) >= 0 ||
            !cookie.Contains('='))
        {
            throw new ProviderException("The Netease cookie is empty or malformed.");
        }

        return cookie;
    }

    private (string Cookie, string UserId) RequiredAuthentication()
    {
        if (!_state.IsAuthenticated || string.IsNullOrWhiteSpace(_cookie) ||
            string.IsNullOrWhiteSpace(_state.UserId))
        {
            throw new ProviderException("Netease is not authenticated. Run 'login netease' first.");
        }

        return (_cookie, _state.UserId);
    }

    private static void EnsureApiSuccess(JsonElement root)
    {
        var code = ReadInt32(root, "code");
        if (code is not null and not 200)
        {
            throw new ProviderException($"Netease API returned code {code}.");
        }
    }

    private static string? ReadString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var child) || child.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return child.ValueKind == JsonValueKind.String ? child.GetString() : child.ToString();
    }

    private static bool TryReadId(JsonElement value, string property, out string id)
    {
        id = ReadString(value, property) ?? string.Empty;
        return id.Length > 0 && id.All(char.IsAsciiDigit);
    }

    private static int? ReadInt32(JsonElement value, string property) =>
        int.TryParse(ReadString(value, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static long? ReadInt64(JsonElement value, string property) =>
        long.TryParse(ReadString(value, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static T Required<T>(T? value) where T : class =>
        value ?? throw new InvalidOperationException("The Netease plugin is not initialized.");
}
