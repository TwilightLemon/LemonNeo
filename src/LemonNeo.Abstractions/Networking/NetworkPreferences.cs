using LemonNeo.Abstractions.Settings;

namespace LemonNeo.Abstractions.Networking;

public enum NetworkProxyMode
{
    System,
    Custom,
    None
}

/// <summary>Host-wide proxy behavior applied to every HTTP client, including plugin clients.</summary>
public sealed record NetworkPreferences(
    NetworkProxyMode Mode = NetworkProxyMode.System,
    string? ProxyAddress = null,
    string? BypassList = null,
    string? Username = null,
    string? Password = null)
{
    public static void Register(ISettingsScope scope)
    {
        scope.Register(nameof(NetworkPreferences), new NetworkPreferences(), Normalize);
    }

    public static NetworkPreferences Normalize(NetworkPreferences value)
    {
        return value with
        {
            Mode = Enum.IsDefined(value.Mode) ? value.Mode : NetworkProxyMode.System,
            ProxyAddress = NormalizeOptionalText(value.ProxyAddress),
            BypassList = NormalizeOptionalText(value.BypassList),
            Username = NormalizeOptionalText(value.Username),
            Password = string.IsNullOrEmpty(value.Password) ? null : value.Password
        };
    }

    /// <summary>Accepts a full proxy URI or a bare <c>host:port</c> pair, which defaults to HTTP.</summary>
    public static bool TryGetProxyUri(string? address, out Uri proxyUri)
    {
        proxyUri = null!;
        var value = NormalizeOptionalText(address);
        if (value is null) return false;
        if (!value.Contains("://", StringComparison.Ordinal)) value = "http://" + value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5") ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            uri.Port is <= 0 or > 65535)
            return false;

        proxyUri = uri;
        return true;
    }

    private static string? NormalizeOptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
