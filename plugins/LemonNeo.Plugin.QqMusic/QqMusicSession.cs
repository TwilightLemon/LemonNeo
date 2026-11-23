using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Plugin.QqMusic;

public sealed partial class QqMusicUserPlugin
{
    private const string SessionKey = "qqmusic-session";
    private static readonly Uri WebCallback = new("https://y.qq.com/portal/wx_redirect.html");
    private const string WechatAppId = "wx48db31d50e334801";
    private static readonly JsonSerializerOptions SessionJson = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private HttpClient? _http;
    private DateTimeOffset _nextRefresh;

    public IReadOnlyList<WebLoginOption> CreateWebLoginOptions()
    {
        var callback = WebCallback + "?surl=" + Uri.EscapeDataString("https://y.qq.com/n/ryqq/profile/like/song");
        var qq = Uri.EscapeDataString(callback + "&login_type=1");
        var wechat = Uri.EscapeDataString(callback + "&login_type=2");
        return
        [
            new("QQ", new Uri("https://graph.qq.com/oauth2.0/show?which=Login&response_type=code&display=pc" +
                              "&client_id=100497308&scope=get_user_info%2Cget_app_friends&redirect_uri=" +
                              qq + "&state=" + Guid.NewGuid().ToString("N")), WebCallback),
            new("WeChat", new Uri("https://open.weixin.qq.com/connect/qrconnect?appid=" + WechatAppId +
                                   "&response_type=code&scope=snsapi_login&redirect_uri=" +
                                   wechat + "&state=" + Guid.NewGuid().ToString("N")), WebCallback)
        ];
    }

    public async Task<AuthenticationState?> CompleteWebLoginAsync(WebLoginRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Callback is not { } callback) return null;
        var query = ReadQuery(callback);
        if (callback.GetLeftPart(UriPartial.Path) != WebCallback.AbsoluteUri ||
            query.GetValueOrDefault("state") != ReadQuery(request.Option.StartUri).GetValueOrDefault("state") ||
            query.GetValueOrDefault("code") is not { Length: > 0 } code)
            throw new ProviderException("QQ Music did not return a valid sign-in response.");
        var wechat = request.Option.StartUri.Host == "open.weixin.qq.com";
        if (query.GetValueOrDefault("login_type") != (wechat ? "2" : "1"))
            throw new ProviderException("QQ Music returned an unexpected sign-in method.");

        var cookies = new CookieContainer();
        foreach (var cookie in request.Cookies)
        {
            // The QQ SSO page may set cookies for other QQ products. Keep the music origin's cookies.
            if (cookie.Domain.TrimStart('.').ToLowerInvariant() is not ("qq.com" or "y.qq.com" or "u.y.qq.com")) continue;
            try { cookies.Add(cookie); }
            catch (CookieException) { }
        }
        var cookieHeader = cookies.GetCookieHeader(QqMusicApiClient.RpcUri);
        var rpc = wechat
            ? await Required(_api).CallAsync("music.login.LoginServer", "Login", new { strAppid = WechatAppId, code },
                cancellationToken, cookieHeader, loginType: 1, responseCookies: cookies)
            : await Required(_api).CallAsync("QQConnectLogin.LoginServer", "QQLogin", new { code },
                cancellationToken, cookieHeader, responseCookies: cookies);
        var loginCookies = ReadCookies(cookies.GetCookieHeader(QqMusicApiClient.RpcUri));
        var authentication = ReadSession(rpc, loginCookies, wechat ? 1 : 2, expectedUserId: null);
        return await SaveLoginAsync(authentication, cancellationToken);
    }

    private async Task<AuthenticationState> SaveLoginAsync(QqMusicCredentials authentication, CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await Required(_api).GetProfileAsync(authentication, cancellationToken);
            var account = new AccountSettings(profile.UserId, profile.Nickname, profile.AvatarUri?.AbsoluteUri);
            await SaveSessionAsync(authentication, account, cancellationToken);
            Required(_settings).Apply<AccountSettings>(_ => account);
            State = new AuthenticationState(true, profile.Nickname, profile.UserId, profile.AvatarUri);
            _logger?.LogInformation("Authenticated QQ Music user {UserId}.", profile.UserId);
            return State;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task RestoreSessionAsync(CancellationToken cancellationToken)
    {
        var vault = Required(_credentials);
        var account = Required(_settings).Get<AccountSettings>();
        if (await vault.GetSecretAsync(SessionKey, cancellationToken) is { } saved)
        {
            var session = JsonSerializer.Deserialize<SavedSession>(saved, SessionJson)
                          ?? throw new ProviderException("QQ Music saved session could not be read.");
            _authentication = session.Credentials;
            account = session.Account;
        }
        else if (await vault.GetSecretAsync(CookieKey, cancellationToken) is { Length: > 0 } cookie &&
                 account.UserId is { Length: > 0 } userId &&
                 long.TryParse(await vault.GetSecretAsync(GtkKey, cancellationToken), out var gtk))
        {
            _authentication = new QqMusicCredentials(cookie, userId, gtk);
        }

        if (_authentication is null) return;
        State = new AuthenticationState(true, account.Nickname ?? account.UserId, account.UserId, TryCreateUri(account.AvatarUri));
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            await EnsureSessionAsync(startup.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _nextRefresh = default;
        }
    }

    private async Task SaveSessionAsync(QqMusicCredentials credentials, AccountSettings account, CancellationToken ct)
    {
        var saved = JsonSerializer.Serialize(new SavedSession(credentials, account), SessionJson);
        await Required(_credentials).SetSecretAsync(SessionKey, saved, ct);
        _authentication = credentials;
        _nextRefresh = DateTimeOffset.UtcNow.AddHours(12);
    }

    private async Task<QqMusicCredentials?> EnsureSessionAsync(CancellationToken ct, QqMusicCredentials? rejected = null)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            if (_authentication is not { } current) return null;
            if (rejected is not null && !ReferenceEquals(current, rejected)) return current;
            if (rejected is null && DateTimeOffset.UtcNow < _nextRefresh) return current;

            var cookies = ReadCookies(current.Cookie);
            var refreshToken = Get(cookies, "psrf_qqrefresh_token", "wxrefresh_token", "refresh_token");
            var refreshKey = Get(cookies, "qm_refresh_key", "refresh_key");
            if (refreshToken is null && refreshKey is null)
            {
                _nextRefresh = DateTimeOffset.UtcNow.AddHours(12);
                if (rejected is null && await VerifySavedSessionAsync(current, ct)) return current;
                MarkSessionExpired();
                return null;
            }

            try
            {
                var loginType = Get(cookies, "tmeLoginType") is { } savedType
                    ? savedType == "1" ? 1 : 2
                    : Get(cookies, "login_type") == "2" || Get(cookies, "wxuin") is not null ? 1 : 2;
                var common = new Dictionary<string, object?>
                {
                    ["openid"] = Get(cookies, "psrf_qqopenid", "wxopenid", "openid") ?? "",
                    ["refresh_token"] = refreshToken ?? "",
                    ["musickey"] = Get(cookies, "qqmusic_key", "qm_keyst"),
                    ["refresh_key"] = refreshKey ?? "",
                    ["loginMode"] = 2
                };
                if (loginType == 1)
                {
                    common["str_musicid"] = current.UserId;
                    common["unionid"] = Get(cookies, "psrf_qqunionid", "unionid") ?? "";
                }
                else
                {
                    common["musicid"] = ulong.Parse(current.UserId, CultureInfo.InvariantCulture);
                    common["access_token"] = Get(cookies, "psrf_qqaccess_token", "access_token") ?? "";
                    common["expired_in"] = long.TryParse(Get(cookies, "psrf_access_token_expiresAt"), out var expires) ? expires : 0L;
                }

                var result = await Required(_api).CallAsync("music.login.LoginServer", "Login", common, ct,
                    current.Cookie, current.UserId, loginType);
                var refreshed = ReadSession(result, cookies, loginType, current.UserId);
                await SaveSessionAsync(refreshed,
                    new AccountSettings(State.UserId, State.AccountName, State.AvatarUri?.AbsoluteUri), CancellationToken.None);
                return refreshed;
            }
            catch (QqSessionExpiredException)
            {
                _nextRefresh = DateTimeOffset.UtcNow.AddHours(1);
                if (rejected is null && await VerifySavedSessionAsync(current, ct)) return current;
                MarkSessionExpired();
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or ProviderException or CookieException ||
                                       ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                _nextRefresh = DateTimeOffset.UtcNow.AddMinutes(5);
                _logger?.LogWarning("QQ Music session refresh failed. The saved session will be retried.");
                return current;
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task<bool> VerifySavedSessionAsync(QqMusicCredentials current, CancellationToken ct)
    {
        try
        {
            await Required(_api).GetProfileAsync(current, ct);
            return true;
        }
        catch (QqSessionExpiredException)
        {
            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException or ProviderException ||
                                          exception is OperationCanceledException && !ct.IsCancellationRequested)
        {
            _nextRefresh = DateTimeOffset.UtcNow.AddMinutes(5);
            return true;
        }
    }

    private void MarkSessionExpired()
    {
        _authentication = null;
        State = new AuthenticationState(false);
    }

    private async Task<T> WithSessionAsync<T>(Func<QqMusicCredentials, CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        const string signIn = "Sign in to QQ Music to access your library.";
        var current = await EnsureSessionAsync(ct) ?? throw new ProviderException(signIn);
        try
        {
            return await operation(current, ct);
        }
        catch (QqSessionExpiredException)
        {
            var next = await EnsureSessionAsync(ct, current);
            if (next is null || next.Cookie == current.Cookie) throw new ProviderException(signIn);
            return await operation(next, ct);
        }
    }

    private static QqMusicCredentials ReadSession(JsonElement result, Dictionary<string, string> cookies, int loginType,
        string? expectedUserId)
    {
        var rawId = ReadField(result, "str_musicid", "musicid", "uin") ??
                    (expectedUserId is null ? Get(cookies, "wxuin", "uin", "musicid", "qqmusic_uin") : expectedUserId);
        var key = ReadField(result, "musickey", "qqmusic_key", "qm_keyst") ??
                  (expectedUserId is null ? Get(cookies, "qqmusic_key", "qm_keyst") : null);
        if (rawId is null || !TryNormalizeUserId(rawId, out var id) || key is null || !IsCookieValue(key) ||
            (expectedUserId is not null && expectedUserId != id))
            throw new ProviderException("QQ Music did not return a complete session.");

        // QQ's web and RPC endpoints read different aliases of the same account credentials.
        foreach (var name in new[] { "uin", "qqmusic_uin", "musicid", "qm_str_musicid" }) cookies[name] = id;
        foreach (var name in new[] { "qqmusic_key", "musickey", "qm_keyst" }) cookies[name] = key;
        cookies[loginType == 1 ? "wxuin" : "uin"] = id;
        cookies["login_type"] = loginType == 1 ? "2" : "1";
        cookies["tmeLoginType"] = loginType.ToString(CultureInfo.InvariantCulture);
        foreach (var (name, fields) in new (string, string[])[]
                 {
                     ("psrf_qqopenid", ["openid", "openId"]),
                     ("psrf_qqunionid", ["unionid", "unionId"]),
                     ("psrf_qqrefresh_token", ["refresh_token", "refreshToken"]),
                     ("psrf_qqaccess_token", ["access_token", "accessToken"]),
                     ("psrf_access_token_expiresAt", ["expired_at", "expiredAt", "expired_in"]),
                     ("qm_refresh_key", ["refresh_key", "refreshKey"])
                 })
            if (ReadField(result, fields) is { } token && IsCookieValue(token)) cookies[name] = token;

        var sessionCookies = cookies.Where(pair => pair.Key is "uin" or "wxuin" or "qqmusic_uin" or "musicid" or
            "qm_str_musicid" or "qqmusic_key" or "musickey" or "qm_keyst" or "login_type" or "tmeLoginType" or
            "psrf_qqopenid" or "psrf_qqunionid" or "psrf_qqrefresh_token" or "psrf_qqaccess_token" or
            "psrf_access_token_expiresAt" or "qm_refresh_key" or "wxopenid" or "wxrefresh_token");
        return new QqMusicCredentials(string.Join("; ", sessionCookies.Select(pair => $"{pair.Key}={pair.Value}")), id, CalculateGtk(key));
    }

    private static string? ReadField(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var field in fields)
            if (value.TryGetProperty(field, out var child) && child.ValueKind is JsonValueKind.String or JsonValueKind.Number &&
                child.ToString() is { Length: > 0 } text) return text;
        return null;
    }

    private static Dictionary<string, string> ReadQuery(Uri uri) =>
        ReadPairs(uri.Query.TrimStart('?') + "&" + uri.Fragment.TrimStart('#'), '&', Uri.UnescapeDataString);

    private static Dictionary<string, string> ReadCookies(string cookie) => ReadPairs(cookie, ';', value => value.Trim('"'));

    private static Dictionary<string, string> ReadPairs(string value, char separator, Func<string, string> decode)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in value.Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            if (index > 0) result[pair[..index].Trim()] = decode(pair[(index + 1)..].Trim());
        }
        return result;
    }

    private static string? Get(Dictionary<string, string> values, params string[] keys) =>
        keys.Select(key => values.GetValueOrDefault(key)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static bool IsCookieValue(string value) => value.IndexOfAny([';', '\r', '\n']) < 0;

    private sealed record SavedSession(QqMusicCredentials Credentials, AccountSettings Account);
}
