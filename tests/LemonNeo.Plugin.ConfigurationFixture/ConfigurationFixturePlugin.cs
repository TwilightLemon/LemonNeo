using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Plugin.ConfigurationFixture;

public sealed record AccountSettings(string State = "Default");

public sealed class ConfigurationFixturePlugin : ILemonPlugin
{
    private ISettingsScope? _settings;
    private IDisposable? _subscription;
    public PluginDescriptor Descriptor { get; } = new("LemonNeo.Plugin.ConfigurationFixture", "Configuration acceptance fixture", "1.0.0");

    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _settings = context.Settings;
        await _settings.RegisterAsync(nameof(AccountSettings), new AccountSettings(), cancellationToken: cancellationToken);
        _subscription = _settings.Subscribe<AccountSettings>(_ => { });
        _settings.Apply<AccountSettings>(_ => new("Initialized"));
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        _settings?.Apply<AccountSettings>(_ => new("Shutdown"));
        _subscription?.Dispose();
        _subscription = null;
        _settings = null;
        return ValueTask.CompletedTask;
    }
}
