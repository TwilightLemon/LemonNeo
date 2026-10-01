using System.Globalization;
using System.Runtime.CompilerServices;
using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Plugin.QqMusic;

public sealed class QqMusicUserPlugin :
    ILemonPlugin,
    IMusicProvider,
    ITrackLookupProvider,
    IPlaylistProvider,
    IPlaylistCollectionProvider,
    IAuthenticationProvider
{
    private const string ProviderId = "qqmusic";
    private const string CookieKey = "y-qq-cookie";
    private const string GtkKey = "g-tk";
    private const string UserIdKey = "user-id";
    private const string NicknameKey = "nickname";
    private const string AvatarKey = "avatar-uri";

    private IPluginStorage? _storage;
    private ICredentialVault? _credentials;
    private QqMusicApiClient? _api;
    private QqMusicPlaybackProvider? _playbackProvider;
    private ILogger<QqMusicUserPlugin>? _logger;
    private QqMusicCredentials? _authentication;
    private AuthenticationState _state = new(false);

    public PluginDescriptor Descriptor { get; } = new(
        "org.lemonneo.qqmusic.user",
        "QQ Music User Library",
        "1.0.0");

    public string Id => ProviderId;

    ProviderDescriptor IMusicProvider.Descriptor { get; } = new(
        "QQ Music",
        "Imports the authenticated user's created, followed, and purchased QQ Music collections.");

    public AuthenticationState State => _state;

    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _storage = context.Storage;
        _credentials = context.Credentials;
        _api = new QqMusicApiClient(context.HttpClientFactory.CreateClient(Descriptor.Id));
        _logger = context.LoggerFactory.CreateLogger<QqMusicUserPlugin>();

        var cookie = await _credentials.GetSecretAsync(CookieKey, cancellationToken);
        var gtkValue = await _credentials.GetSecretAsync(GtkKey, cancellationToken);
        var userId = await _storage.GetAsync(UserIdKey, cancellationToken);
        var nickname = await _storage.GetAsync(NicknameKey, cancellationToken);
        var avatarValue = await _storage.GetAsync(AvatarKey, cancellationToken);
        if (!string.IsNullOrWhiteSpace(cookie) &&
            long.TryParse(gtkValue, NumberStyles.None, CultureInfo.InvariantCulture, out var gtk) &&
            !string.IsNullOrWhiteSpace(userId))
        {
            _authentication = new QqMusicCredentials(cookie, userId, gtk);
            _state = new AuthenticationState(
                true,
                nickname ?? userId,
                userId,
                TryCreateUri(avatarValue));
        }

        var playbackClient = context.HttpClientFactory.CreateClient("org.lemonneo.qqmusic.playback");
        playbackClient.Timeout = TimeSpan.FromSeconds(20);
        _playbackProvider = new QqMusicPlaybackProvider(
            playbackClient,
            () => _authentication,
            context.LoggerFactory.CreateLogger<QqMusicPlaybackProvider>());
        context.RegisterProvider(this);
        context.RegisterProvider(_playbackProvider);
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _authentication = null;
        _state = new AuthenticationState(false);
        _api = null;
        _playbackProvider = null;
        _storage = null;
        _credentials = null;
        _logger = null;
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
            throw new ProviderException("QQ Music login requires a complete y.qq.com cookie string.");
        }

        var parsed = ParseCookie(rawCookie);
        var authentication = new QqMusicCredentials(parsed.Cookie, parsed.UserId, parsed.Gtk);
        var profile = await Required(_api).GetProfileAsync(authentication, cancellationToken);

        var credentials = Required(_credentials);
        var storage = Required(_storage);
        await credentials.SetSecretAsync(CookieKey, authentication.Cookie, cancellationToken);
        await credentials.SetSecretAsync(
            GtkKey,
            authentication.Gtk.ToString(CultureInfo.InvariantCulture),
            cancellationToken);
        await storage.SetAsync(UserIdKey, profile.UserId, cancellationToken);
        await storage.SetAsync(NicknameKey, profile.Nickname, cancellationToken);
        if (profile.AvatarUri is not null)
        {
            await storage.SetAsync(AvatarKey, profile.AvatarUri.AbsoluteUri, cancellationToken);
        }
        else
        {
            await storage.DeleteAsync(AvatarKey, cancellationToken);
        }

        _authentication = authentication;
        _state = new AuthenticationState(
            true,
            profile.Nickname,
            profile.UserId,
            profile.AvatarUri);
        _logger?.LogInformation("Authenticated QQ Music user {UserId}.", profile.UserId);
        return _state;
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var credentials = Required(_credentials);
        var storage = Required(_storage);
        await credentials.DeleteSecretAsync(CookieKey, cancellationToken);
        await credentials.DeleteSecretAsync(GtkKey, cancellationToken);
        await storage.DeleteAsync(UserIdKey, cancellationToken);
        await storage.DeleteAsync(NicknameKey, cancellationToken);
        await storage.DeleteAsync(AvatarKey, cancellationToken);
        _authentication = null;
        _state = new AuthenticationState(false);
    }

    public async IAsyncEnumerable<RemotePlaylistCollection> GetUserPlaylistCollectionsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var authentication = RequiredAuthentication();
        var api = Required(_api);
        var profile = await api.GetProfileAsync(authentication, cancellationToken);
        yield return new RemotePlaylistCollection(
            "created",
            "我创建的歌单",
            profile.CreatedPlaylists);

        var followed = await api.GetFollowedPlaylistsAsync(authentication, cancellationToken);
        yield return new RemotePlaylistCollection(
            "followed",
            "我收藏的歌单",
            followed);

        var purchased = await api.GetPurchasedAlbumsAsync(authentication, cancellationToken);
        yield return new RemotePlaylistCollection(
            "purchased-albums",
            "我购买的专辑",
            purchased);
    }

    public async IAsyncEnumerable<RemotePlaylistSummary> GetUserPlaylistsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var collection in GetUserPlaylistCollectionsAsync(cancellationToken))
        {
            foreach (var playlist in collection.Playlists)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return playlist;
            }
        }
    }

    public Task<RemotePlaylist?> GetPlaylistAsync(
        string playlistId,
        CancellationToken cancellationToken = default)
    {
        var (kind, remoteId) = ParseRemotePlaylistId(playlistId);
        var authentication = RequiredAuthentication();
        var api = Required(_api);
        return kind switch
        {
            "playlist" => api.GetPlaylistAsync(authentication, remoteId, cancellationToken),
            "album" => api.GetAlbumAsync(authentication, remoteId, cancellationToken),
            _ => throw new ProviderException($"Unsupported QQ Music collection type '{kind}'.")
        };
    }

    public Task<Track?> GetTrackAsync(
        string trackId,
        CancellationToken cancellationToken = default) =>
        Required(_api).GetTrackAsync(_authentication, trackId, cancellationToken);

    public static long CalculateGtk(string qqmusicKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qqmusicKey);
        long hash = 5381;
        unchecked
        {
            foreach (var character in qqmusicKey)
            {
                hash += (hash << 5) + character;
            }
        }

        return hash & 0x7fffffff;
    }

    private static ParsedQqCookie ParseCookie(string value)
    {
        var cookie = value.Trim();
        if (cookie.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
        {
            cookie = cookie["Cookie:".Length..].Trim();
        }

        if (cookie.Length == 0 || cookie.Length > 65536 || cookie.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new ProviderException("The QQ Music cookie is empty or malformed.");
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = segment[..separator].Trim();
            var cookieValue = segment[(separator + 1)..].Trim().Trim('"');
            if (name.Length > 0)
            {
                values[name] = cookieValue;
            }
        }

        if (!values.TryGetValue("qqmusic_key", out var qqmusicKey) ||
            string.IsNullOrWhiteSpace(qqmusicKey))
        {
            throw new ProviderException("The QQ Music cookie does not contain qqmusic_key.");
        }

        string? userId = null;
        foreach (var cookieName in new[] { "uin", "wxuin" })
        {
            if (values.TryGetValue(cookieName, out var rawUserId) &&
                TryNormalizeUserId(rawUserId, out var candidate))
            {
                userId = candidate;
                break;
            }
        }

        if (userId is null)
        {
            throw new ProviderException("The QQ Music cookie does not contain a valid uin or wxuin.");
        }

        return new ParsedQqCookie(cookie, userId, CalculateGtk(qqmusicKey));
    }

    private static bool TryNormalizeUserId(string value, out string userId)
    {
        var normalized = value.Trim().Trim('"');
        if (normalized.StartsWith('o') || normalized.StartsWith('O'))
        {
            normalized = normalized[1..];
        }

        if (ulong.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
        {
            userId = parsed.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        userId = string.Empty;
        return false;
    }

    private static (string Kind, string Id) ParseRemotePlaylistId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ProviderException("A QQ Music playlist or album id is required.");
        }

        var separator = value.IndexOf(':');
        var kind = separator < 0 ? "playlist" : value[..separator].ToLowerInvariant();
        var remoteId = separator < 0 ? value : value[(separator + 1)..];
        var valid = kind switch
        {
            "playlist" => remoteId.All(char.IsAsciiDigit),
            "album" => remoteId.All(char.IsAsciiLetterOrDigit),
            _ => false
        };
        if (!valid || remoteId.Length == 0 || remoteId.Length > 128)
        {
            throw new ProviderException(
                "QQ Music ids must use playlist:<numeric-id> or album:<album-mid>.");
        }

        return (kind, remoteId);
    }

    private QqMusicCredentials RequiredAuthentication() =>
        _state.IsAuthenticated && _authentication is not null
            ? _authentication
            : throw new ProviderException("QQ Music is not authenticated. Run 'login qqmusic' first.");

    private static Uri? TryCreateUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static T Required<T>(T? value) where T : class =>
        value ?? throw new InvalidOperationException("The QQ Music plugin is not initialized.");

    private sealed record ParsedQqCookie(string Cookie, string UserId, long Gtk);
}
