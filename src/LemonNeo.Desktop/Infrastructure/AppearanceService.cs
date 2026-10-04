using Avalonia.Media;
using LemonNeo.Configuration;

namespace LemonNeo.Desktop.Infrastructure;

public enum ThemePreference { System, Light, Dark }
public enum LyricAlignment { Center, Left }

public sealed record UiPreferences(
    ThemePreference Theme = ThemePreference.System,
    LanguagePreference Language = LanguagePreference.System,
    bool ReduceMotion = false,
    double LyricFontSize = 24,
    bool ShowRomanization = true,
    bool ShowTranslation = true,
    LyricAlignment LyricAlignment = LyricAlignment.Center,
    bool BoldLyrics = false,
    ThemeColorPreset ThemeColor = ThemeColorPreset.Classic,
    bool UseArtworkColors = false);

/// <summary>Owns live UI preferences. The default follows the operating system.</summary>
public sealed class AppearanceService(
    Avalonia.Application application,
    LocalizationService localization,
    IAppConfigurationStore store) : IDisposable
{
    public const string ConfigurationKey = "desktop.ui";
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    public ThemeColorService Colors { get; } = new(application);
    public UiPreferences Current { get; private set; } = new();
    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var preferences = await store.GetAsync<UiPreferences>(ConfigurationKey, cancellationToken) ?? new();
        Apply(preferences, animate: false);
    }

    public Task SetAsync(UiPreferences preferences, CancellationToken cancellationToken = default) =>
        UpdateAsync(_ => preferences, cancellationToken);

    public async Task UpdateAsync(Func<UiPreferences, UiPreferences> update, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var preferences = Normalize(update(Current));
            // Persist first: a failed write leaves the UI and saved settings in agreement.
            await store.SetAsync(ConfigurationKey, preferences, cancellationToken);
            Apply(preferences);
        }
        finally { _saveGate.Release(); }
    }

    private static UiPreferences Normalize(UiPreferences value) => value with
    {
        Theme = Enum.IsDefined(value.Theme) ? value.Theme : ThemePreference.System,
        Language = Enum.IsDefined(value.Language) ? value.Language : LanguagePreference.System,
        LyricFontSize = double.IsFinite(value.LyricFontSize) ? Math.Clamp(value.LyricFontSize, 16, 48) : 24,
        LyricAlignment = Enum.IsDefined(value.LyricAlignment) ? value.LyricAlignment : LyricAlignment.Center,
        ThemeColor = Enum.IsDefined(value.ThemeColor) ? value.ThemeColor : ThemeColorPreset.Classic
    };

    public void SetArtworkColor(Color? color) => Colors.SetArtworkColor(color);

    private void Apply(UiPreferences preferences, bool animate = true)
    {
        Current = Normalize(preferences);
        Colors.ApplyPreferences(Current, animate);
        application.Resources["Motion.Fast"] = TimeSpan.FromMilliseconds(Current.ReduceMotion ? 0 : 140);
        application.Resources["Motion.Normal"] = TimeSpan.FromMilliseconds(Current.ReduceMotion ? 0 : 220);
        localization.Apply(Current.Language);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Colors.Dispose();
}
