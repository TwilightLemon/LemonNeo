using System.Net;

namespace LemonNeo.Abstractions.Providers;

public sealed record WebLoginOption(string Name, Uri StartUri, Uri? CallbackUri = null);

public sealed record WebLoginRequest(WebLoginOption Option, Uri? Callback, IReadOnlyList<Cookie> Cookies);

/// <summary>The host opens the sign-in page. The provider decides when the credentials are ready and saves them.</summary>
public interface IWebAuthenticationProvider : IAuthenticationProvider
{
    IReadOnlyList<WebLoginOption> CreateWebLoginOptions();

    /// <returns>The verified account, or null while the user is still signing in.</returns>
    Task<AuthenticationState?> CompleteWebLoginAsync(WebLoginRequest request,
        CancellationToken cancellationToken = default);
}
