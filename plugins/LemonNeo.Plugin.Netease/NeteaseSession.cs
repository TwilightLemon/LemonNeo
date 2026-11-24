using System.Net;
using System.Text.Json;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Plugin.Netease;

public sealed partial class NeteaseUserPlugin
{
    public IReadOnlyList<WebLoginOption> CreateWebLoginOptions() =>
        [new("Netease Music", new Uri(Site, "#/my/"))];

    public async Task<AuthenticationState?> CompleteWebLoginAsync(WebLoginRequest request,
        CancellationToken cancellationToken = default)
    {
        var cookies = new CookieContainer();
        foreach (var cookie in request.Cookies)
        {
            if (cookie.Name is not ("MUSIC_U" or "MUSIC_R_T" or "__csrf" or "os" or "appver")) continue;
            var domain = cookie.Domain.TrimStart('.');
            if (!Site.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) &&
                !Site.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                cookies.Add(cookie);
            }
            catch (CookieException)
            {
                // Browser cookies can contain characters rejected by .NET's cookie container.
            }
        }

        var accountUri = new Uri(Site, "api/nuser/account/get");
        if (cookies.GetCookies(accountUri)["MUSIC_U"] is not { Value.Length: > 0 }) return null;
        return await AuthenticateAsync(new AuthenticationRequest(AuthMethod.Cookie,
            new Dictionary<string, string> { ["cookie"] = cookies.GetCookieHeader(accountUri) }), cancellationToken);
    }

    private async Task<string?> GetCookieAsync(CancellationToken cancellationToken, string? rejected = null)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            if (_cookie is not { Length: > 0 } cookie) return null;
            if (rejected is null && DateTimeOffset.UtcNow < _nextRefresh) return cookie;
            if (rejected is not null && rejected != cookie) return cookie;

            try
            {
                var cookies = new CookieContainer();
                foreach (var pair in cookie.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    try { cookies.SetCookies(Site, pair); }
                    catch (CookieException) { }
                }

                using var response = await RequestJsonAsync("api/login/token/refresh", cookie, cancellationToken,
                    cookies, HttpMethod.Post);
                var code = ReadInt32(response.RootElement, "code");
                if (IsExpiredSession(code))
                {
                    // Some older cookies cannot be renewed but can still authenticate.
                    using var profile = await RequestJsonAsync("api/nuser/account/get", cookie, cancellationToken);
                    if (!profile.RootElement.TryGetProperty("profile", out var value) ||
                        value.ValueKind != JsonValueKind.Object || !TryReadId(value, "userId", out _))
                    {
                        var profileCode = ReadInt32(profile.RootElement, "code");
                        if (IsExpiredSession(profileCode) || profileCode == 200)
                        {
                            _cookie = null;
                            State = new AuthenticationState(false);
                            return null;
                        }
                        EnsureApiSuccess(profile.RootElement);
                    }
                    _nextRefresh = DateTimeOffset.UtcNow.AddHours(1);
                    return cookie;
                }
                if (code != 200) throw new ProviderException($"Netease session refresh returned code {code}.");
                var refreshed = cookies.GetCookieHeader(Site);
                if (cookies.GetCookies(Site)["MUSIC_U"] is not { Value.Length: > 0 })
                    throw new ProviderException("Netease did not return an authenticated session.");

                AccountSettings? account = null;
                if (!State.IsAuthenticated)
                {
                    var profile = await GetAccountAsync(refreshed, cancellationToken);
                    account = new AccountSettings(profile.UserId, profile.Nickname, profile.AvatarUri?.AbsoluteUri);
                }
                if (refreshed != cookie)
                    await Required(_credentials).SetSecretAsync(CookieKey, refreshed, CancellationToken.None);
                if (account is not null)
                {
                    Required(_settings).Apply<AccountSettings>(_ => account);
                    State = new AuthenticationState(true, account.Nickname, account.UserId, TryCreateAvatarUri(account.AvatarUri));
                }
                _cookie = refreshed;
                _nextRefresh = DateTimeOffset.UtcNow.AddHours(12);
                return refreshed;
            }
            catch (Exception exception) when (exception is HttpRequestException or ProviderException or CookieException ||
                                              exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _nextRefresh = DateTimeOffset.UtcNow.AddMinutes(5);
                _logger?.LogWarning("Netease session refresh failed. The saved session will be retried.");
                return cookie;
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task<(string Cookie, string UserId)> RequiredAuthenticationAsync(CancellationToken cancellationToken)
    {
        var cookie = await GetCookieAsync(cancellationToken);
        if (cookie is null || State is not { IsAuthenticated: true, UserId: { } id })
            throw new ProviderException("Sign in to Netease Music to access your library.");
        return (cookie, id);
    }

    private async Task<JsonDocument> GetAuthenticatedJsonAsync(string path, CancellationToken cancellationToken)
    {
        var (cookie, _) = await RequiredAuthenticationAsync(cancellationToken);
        var document = await RequestJsonAsync(path, cookie, cancellationToken);
        if (!IsExpiredSession(ReadInt32(document.RootElement, "code"))) return document;
        document.Dispose();
        if (await GetCookieAsync(cancellationToken, cookie) is not { } refreshed || refreshed == cookie)
            throw new ProviderException("Sign in again to access your Netease Music library.");
        return await RequestJsonAsync(path, refreshed, cancellationToken);
    }

    private static bool IsExpiredSession(int? code) => code is 301 or 401;
}
