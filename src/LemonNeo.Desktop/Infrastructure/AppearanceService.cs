using Avalonia;
using Avalonia.Styling;
using LemonNeo.Configuration;

namespace LemonNeo.Desktop.Infrastructure;

public enum ThemePreference { System, Light, Dark }

public sealed record UiPreferences(
    ThemePreference Theme = ThemePreference.System,
    LanguagePreference Language = LanguagePreference.System,
    bool ReduceMotion = false);

/// <summary>Owns live UI preferences. The default follows the operating system.</summary>
public sealed class AppearanceService(
    Avalonia.Application application,
    LocalizationService localization,
    IAppConfigurationStore store)
{
    public const string ConfigurationKey = "desktop.ui";
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    public UiPreferences Current { get; private set; } = new();
    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var preferences = await store.GetAsync<UiPreferences>(ConfigurationKey, cancellationToken) ?? new();
        Apply(preferences);
    }

    public async Task SetAsync(UiPreferences preferences, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            // Persist first: a failed write leaves the UI and saved settings in agreement.
            await store.SetAsync(ConfigurationKey, Normalize(preferences), cancellationToken);
            Apply(preferences);
        }
        finally { _saveGate.Release(); }
    }

    private static UiPreferences Normalize(UiPreferences value) => value with
    {
        Theme = Enum.IsDefined(value.Theme) ? value.Theme : ThemePreference.System,
        Language = Enum.IsDefined(value.Language) ? value.Language : LanguagePreference.System
    };

    private void Apply(UiPreferences preferences)
    {
        Current = Normalize(preferences);
        application.RequestedThemeVariant = Current.Theme switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
        application.Resources["Motion.Fast"] = TimeSpan.FromMilliseconds(Current.ReduceMotion ? 0 : 140);
        application.Resources["Motion.Normal"] = TimeSpan.FromMilliseconds(Current.ReduceMotion ? 0 : 220);
        localization.Apply(Current.Language);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
