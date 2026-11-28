using System.Globalization;
using System.Runtime.CompilerServices;
using LemonNeo.Abstractions.Plugins;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Abstractions.Storage;
using LemonNeo.Core.Exceptions;
using LemonNeo.Core.Music;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Plugin.QqMusic;

public sealed record AccountSettings(string? UserId = null, string? Nickname = null, string? AvatarUri = null);

public sealed partial class QqMusicUserPlugin :
    ILemonPlugin,
    IMusicProvider,
    ITrackLookupProvider,
    IPlaylistProvider,
    IPlaylistCollectionProvider,
    IWebAuthenticationProvider,
    ISearchProvider,
    ICatalogProvider
{
    private const string ProviderId = "qqmusic";
    private const string CookieKey = "y-qq-cookie";
    private const string GtkKey = "g-tk";
    private QqMusicApiClient? _api;
    private QqMusicCredentials? _authentication;
    private ICredentialVault? _credentials;
    private ILogger<QqMusicUserPlugin>? _logger;
    private QqMusicPlaybackProvider? _playbackProvider;

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
            throw new ProviderException("QQ Music login requires a complete y.qq.com cookie string.");

        var parsed = ParseCookie(rawCookie);
        return await SaveLoginAsync(new QqMusicCredentials(parsed.Cookie, parsed.UserId, parsed.Gtk),
            cancellationToken);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            var credentials = Required(_credentials);
            await credentials.DeleteSecretAsync(CookieKey, cancellationToken);
            await credentials.DeleteSecretAsync(GtkKey, cancellationToken);
            await credentials.DeleteSecretAsync(SessionKey, cancellationToken);
            Required(_settings).Apply<AccountSettings>(_ => new AccountSettings());
            _authentication = null;
            State = new AuthenticationState(false);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public PluginDescriptor Descriptor { get; } = new(
        "LemonNeo.Plugin.QqMusic",
        "QQ Music User Library",
        "1.0.0");

    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _settings = context.Settings;
        await _settings.RegisterAsync(nameof(AccountSettings), new AccountSettings(),
            cancellationToken: cancellationToken);
        _credentials = context.Credentials;
        _http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _api = new QqMusicApiClient(_http);
        _logger = context.LoggerFactory.CreateLogger<QqMusicUserPlugin>();

        await RestoreSessionAsync(cancellationToken);
        var playbackClient = context.HttpClientFactory.CreateClient("LemonNeo.Plugin.QqMusic.Playback");
        playbackClient.Timeout = TimeSpan.FromSeconds(20);
        _playbackProvider = new QqMusicPlaybackProvider(
            playbackClient,
            EnsureSessionAsync,
            context.LoggerFactory.CreateLogger<QqMusicPlaybackProvider>());
        context.RegisterProvider(this);
        context.RegisterProvider(_playbackProvider);
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _authentication = null;
        State = new AuthenticationState(false);
        _api = null;
        _http?.Dispose();
        _http = null;
        _playbackProvider = null;
        _settings = null;
        _credentials = null;
        _logger = null;
        return ValueTask.CompletedTask;
    }

    public string Id => ProviderId;

    ProviderDescriptor IMusicProvider.Descriptor { get; } = new(
        "QQ Music",
        "Imports the authenticated user's created and followed playlists, followed albums, and purchased QQ Music albums.");

    public async IAsyncEnumerable<RemotePlaylistCollection> GetUserPlaylistCollectionsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var api = Required(_api);
        var profile = await WithSessionAsync(api.GetProfileAsync, cancellationToken);
        yield return new RemotePlaylistCollection(
            "created",
            "我创建的歌单",
            profile.CreatedPlaylists);

        var followed = await WithSessionAsync(api.GetFollowedPlaylistsAsync, cancellationToken);
        yield return new RemotePlaylistCollection(
            "followed",
            "我收藏的歌单",
            followed);

        var followedAlbums = await WithSessionAsync(api.GetFollowedAlbumsAsync, cancellationToken);
        yield return new RemotePlaylistCollection(
            "followed-albums",
            "我收藏的专辑",
            followedAlbums);

        var purchased = await WithSessionAsync(api.GetPurchasedAlbumsAsync, cancellationToken);
        yield return new RemotePlaylistCollection(
            "purchased-albums",
            "我购买的专辑",
            purchased);
    }

    public async IAsyncEnumerable<RemotePlaylistSummary> GetUserPlaylistsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var collection in GetUserPlaylistCollectionsAsync(cancellationToken))
        foreach (var playlist in collection.Playlists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return playlist;
        }
    }

    public Task<RemotePlaylist?> GetPlaylistAsync(
        string playlistId,
        CancellationToken cancellationToken = default)
    {
        var (kind, remoteId) = ParseRemotePlaylistId(playlistId);
        var api = Required(_api);
        return kind switch
        {
            "playlist" => WithSessionAsync((session, ct) => api.GetPlaylistAsync(session, remoteId, ct), cancellationToken),
            "album" => WithSessionAsync((session, ct) => api.GetAlbumAsync(session, remoteId, ct), cancellationToken),
            _ => throw new ProviderException($"Unsupported QQ Music collection type '{kind}'.")
        };
    }

    public Task<Track?> GetTrackAsync(
        string trackId,
        CancellationToken cancellationToken = default)
    {
        return Required(_api).GetTrackAsync(_authentication, trackId, cancellationToken);
    }

    public static long CalculateGtk(string qqmusicKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qqmusicKey);
        long hash = 5381;
        unchecked
        {
            foreach (var character in qqmusicKey) hash += (hash << 5) + character;
        }

        return hash & 0x7fffffff;
    }

    private static ParsedQqCookie ParseCookie(string value)
    {
        var cookie = value.Trim();
        if (cookie.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
            cookie = cookie["Cookie:".Length..].Trim();

        if (cookie.Length == 0 || cookie.Length > 65536 || cookie.IndexOfAny(['\r', '\n']) >= 0)
            throw new ProviderException("The QQ Music cookie is empty or malformed.");

        var values = ReadCookies(cookie);

        if (!values.TryGetValue("qqmusic_key", out var qqmusicKey) ||
            string.IsNullOrWhiteSpace(qqmusicKey))
            throw new ProviderException("The QQ Music cookie does not contain qqmusic_key.");

        string? userId = null;
        foreach (var cookieName in new[] { "uin", "wxuin" })
            if (values.TryGetValue(cookieName, out var rawUserId) &&
                TryNormalizeUserId(rawUserId, out var candidate))
            {
                userId = candidate;
                break;
            }

        if (userId is null) throw new ProviderException("The QQ Music cookie does not contain a valid uin or wxuin.");

        return new ParsedQqCookie(cookie, userId, CalculateGtk(qqmusicKey));
    }

    private static bool TryNormalizeUserId(string value, out string userId)
    {
        var normalized = value.Trim().Trim('"');
        if (normalized.StartsWith('o') || normalized.StartsWith('O')) normalized = normalized[1..];

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
            throw new ProviderException("A QQ Music playlist or album id is required.");

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
            throw new ProviderException(
                "QQ Music ids must use playlist:<numeric-id> or album:<album-mid>.");

        return (kind, remoteId);
    }



    private static Uri? TryCreateUri(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static T Required<T>(T? value) where T : class
    {
        return value ?? throw new InvalidOperationException("The QQ Music plugin is not initialized.");
    }

    private sealed record ParsedQqCookie(string Cookie, string UserId, long Gtk);
}
