using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Plugin.Runtime;

namespace LemonNeo.Desktop.ViewModels;

public sealed class SettingsViewModel : PageViewModel
{
    private AppearanceService _appearance => Context.Appearance;
    public SettingsViewModel(DesktopContext context) : base(context, DesktopRoutes.Settings)
    {
        ThemeCommand = new(p => SavePreferencesAsync(_appearance.Current with { Theme = Enum.Parse<ThemePreference>(p!) }));
        LanguageCommand = new(p => SavePreferencesAsync(_appearance.Current with { Language = Enum.Parse<LanguagePreference>(p!) }));
        _appearance.Changed += OnAppearanceChanged;
    }
    public AsyncRelayCommand<string> ThemeCommand { get; }
    public AsyncRelayCommand<string> LanguageCommand { get; }
    public IEnumerable<string> PluginDescriptions => _runtime.Get<PluginLoader>().Loaded.Select(p => $"{p.Manifest.Name}  ·  {p.Manifest.Version}");
    public bool IsThemeSystem => _appearance.Current.Theme == ThemePreference.System;
    public bool IsThemeLight => _appearance.Current.Theme == ThemePreference.Light;
    public bool IsThemeDark => _appearance.Current.Theme == ThemePreference.Dark;
    public bool IsLanguageSystem => _appearance.Current.Language == LanguagePreference.System;
    public bool IsLanguageChinese => _appearance.Current.Language == LanguagePreference.Chinese;
    public bool IsLanguageEnglish => _appearance.Current.Language == LanguagePreference.English;
    public bool ReduceMotion
    {
        get => _appearance.Current.ReduceMotion;
        set { if (value != ReduceMotion) _ = SavePreferencesAsync(_appearance.Current with { ReduceMotion = value }); }
    }
    public string PluginSummary => _text.Format("PluginCount", _runtime.Get<PluginLoader>().Loaded.Count);
    public string LibraryPath => _runtime.Get<DesktopOptions>().LibraryDirectory;
    public string ConfigurationPath => _runtime.Paths.ConfigurationDirectory;
    public string CachePath => _runtime.Paths.CacheDirectory;

    private Task SavePreferencesAsync(UiPreferences preferences) => RunOperationAsync(async ct =>
    {
        try
        {
            await _appearance.SetAsync(preferences, ct);
            SetStatus("Saved");
        }
        finally { RefreshAppearanceProperties(); }
    }, allowUninitialized: true);

    private void OnAppearanceChanged(object? sender, EventArgs args) => RefreshAppearanceProperties();
    private void RefreshAppearanceProperties()
    {
        foreach (var name in new[] { nameof(IsThemeSystem), nameof(IsThemeLight), nameof(IsThemeDark), nameof(IsLanguageSystem), nameof(IsLanguageChinese), nameof(IsLanguageEnglish), nameof(ReduceMotion), nameof(PluginSummary), nameof(PluginDescriptions) }) OnPropertyChanged(name);
    }
    protected override void RefreshLocalizedProperties() { base.RefreshLocalizedProperties(); RefreshAppearanceProperties(); }
    public override async ValueTask DisposeAsync() { _appearance.Changed -= OnAppearanceChanged; await base.DisposeAsync(); }
}
