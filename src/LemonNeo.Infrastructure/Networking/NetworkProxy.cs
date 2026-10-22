using System.Net;
using System.Text.RegularExpressions;
using LemonNeo.Abstractions.Networking;
using LemonNeo.Abstractions.Settings;

namespace LemonNeo.Infrastructure.Networking;

/// <summary>
///     Process-wide proxy that reads the current network preferences for every request.
///     It is installed as <see cref="HttpClient.DefaultProxy" /> so plugin code that
///     creates its own clients is covered as well.
/// </summary>
internal sealed class NetworkProxy : IWebProxy, IDisposable
{
    // Captured before the host installs this instance, so System mode keeps delegating to the OS.
    private static readonly IWebProxy SystemProxy = HttpClient.DefaultProxy;
    private readonly ISettingsScope _settings;
    private readonly IDisposable _subscription;
    private ICredentials? _credentials;
    private volatile Snapshot _snapshot;

    public NetworkProxy(IAppSettingsService settings)
    {
        _settings = settings.ForPackage(PackageNames.Application);
        NetworkPreferences.Register(_settings);
        _snapshot = Snapshot.Create(_settings.Get<NetworkPreferences>(), SystemProxy);
        _subscription = _settings.Subscribe<NetworkPreferences>(preferences =>
            _snapshot = Snapshot.Create(preferences, SystemProxy));
    }

    public ICredentials? Credentials
    {
        get => _credentials ?? _snapshot.Proxy?.Credentials;
        set => _credentials = value;
    }

    public void Dispose()
    {
        _subscription.Dispose();
    }

    public Uri GetProxy(Uri destination)
    {
        return _snapshot.Proxy?.GetProxy(destination) ?? destination;
    }

    public bool IsBypassed(Uri host)
    {
        return _snapshot.Proxy?.IsBypassed(host) ?? true;
    }

    private sealed class Snapshot(IWebProxy? proxy)
    {
        public IWebProxy? Proxy { get; } = proxy;

        public static Snapshot Create(NetworkPreferences preferences, IWebProxy systemProxy)
        {
            return preferences.Mode switch
            {
                NetworkProxyMode.System => new Snapshot(systemProxy),
                NetworkProxyMode.Custom => new Snapshot(CreateCustom(preferences)),
                _ => new Snapshot(null)
            };
        }

        private static IWebProxy? CreateCustom(NetworkPreferences preferences)
        {
            if (!NetworkPreferences.TryGetProxyUri(preferences.ProxyAddress, out var address)) return null;

            var proxy = new WebProxy(address)
            {
                BypassProxyOnLocal = false,
                BypassList = CreateBypassList(preferences.BypassList)
            };
            if (address.UserInfo.Length > 0)
            {
                var parts = address.UserInfo.Split(':', 2);
                proxy.Credentials = new NetworkCredential(
                    Uri.UnescapeDataString(parts[0]),
                    parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
            }
            else if (!string.IsNullOrEmpty(preferences.Username))
            {
                proxy.Credentials = new NetworkCredential(preferences.Username, preferences.Password);
            }

            return proxy;
        }

        private static string[] CreateBypassList(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return [];
            // WebProxy matches these expressions against the URI origin, not the bare host name.
            return value
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(entry =>
                    @"(?i)^[a-z][a-z0-9+.-]*://(?:[^@/]*@)?" +
                    Regex.Escape(entry).Replace("\\*", ".*", StringComparison.Ordinal) +
                    @"(?::\d+)?$")
                .ToArray();
        }
    }
}
