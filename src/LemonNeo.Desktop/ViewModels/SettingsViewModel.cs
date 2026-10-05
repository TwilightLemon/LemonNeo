using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Plugin.Runtime;

namespace LemonNeo.Desktop.ViewModels;

public sealed class SettingsViewModel : PageViewModel
{
    private AppearanceService _appearance => Context.Appearance;
    private UiPreferences _preferences;
    private int _pendingPreferenceSaves;
    private readonly List<Task> _preferenceTasks = [];
    private bool _closing;
    public SettingsViewModel(DesktopContext context) : base(context, DesktopRoutes.Settings)
    {
        _preferences = _appearance.Current;
        ThemeCommand = new(p => SavePreferencesAsync(current => current with { Theme = Enum.Parse<ThemePreference>(p!) }),
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        LanguageCommand = new(p => SavePreferencesAsync(current => current with { Language = Enum.Parse<LanguagePreference>(p!) }),
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        ThemeColors = ThemePalette.BuiltIn.Select(p => new ThemeColorOption(p, _appearance, _text,
            id => SavePreferencesAsync(current => current with { ThemeColor = id }))).ToArray();
        _appearance.Changed += OnAppearanceChanged;
        _appearance.Colors.Changed += OnColorChanged;
    }
    public AsyncRelayCommand<string> ThemeCommand { get; }
    public AsyncRelayCommand<string> LanguageCommand { get; }
    public IReadOnlyList<ThemeColorOption> ThemeColors { get; }
    public bool UseArtworkColors
    {
        get => _preferences.UseArtworkColors;
        set
        {
            if (_closing || value == UseArtworkColors) return;
            _preferences = _preferences with { UseArtworkColors = value };
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThemeColorStatus));
            _ = SavePreferencesAsync(current => current with { UseArtworkColors = value });
        }
    }
    public string ThemeColorStatus => _appearance.Colors.IsUsingArtwork
        ? _text["ThemeColorUsingArtwork"]
        : _text.Format(UseArtworkColors ? "ThemeColorFallbackStatus" : "ThemeColorBuiltInStatus", _text[ThemePalette.Get(_appearance.Current.ThemeColor).NameKey]);
    public IEnumerable<string> PluginDescriptions => _runtime.Get<PluginLoader>().Loaded.Select(p => $"{p.Manifest.Name}  ·  {p.Manifest.Version}");
    public bool IsThemeSystem => _appearance.Current.Theme == ThemePreference.System;
    public bool IsThemeLight => _appearance.Current.Theme == ThemePreference.Light;
    public bool IsThemeDark => _appearance.Current.Theme == ThemePreference.Dark;
    public bool IsLanguageSystem => _appearance.Current.Language == LanguagePreference.System;
    public bool IsLanguageChinese => _appearance.Current.Language == LanguagePreference.Chinese;
    public bool IsLanguageEnglish => _appearance.Current.Language == LanguagePreference.English;
    public bool ReduceMotion
    {
        get => _preferences.ReduceMotion;
        set
        {
            if (_closing || value == ReduceMotion) return;
            _preferences = _preferences with { ReduceMotion = value };
            OnPropertyChanged();
            _ = SavePreferencesAsync(current => current with { ReduceMotion = value });
        }
    }
    public string PluginSummary => _text.Format("PluginCount", _runtime.Get<PluginLoader>().Loaded.Count);
    public string LibraryPath => _runtime.Get<DesktopOptions>().LibraryDirectory;
    public string ConfigurationPath => _runtime.Paths.ConfigurationDirectory;
    public string CachePath => _runtime.Paths.CacheDirectory;

    private Task SavePreferencesAsync(Func<UiPreferences, UiPreferences> update)
    {
        if (_closing) return Task.CompletedTask;
        if (_pendingPreferenceSaves == 0) SetError(null);
        _pendingPreferenceSaves++;
        _preferenceTasks.RemoveAll(task => task.IsCompleted);
        var save = SavePreferencesCoreAsync(update);
        _preferenceTasks.Add(save);
        return save;
    }

    private async Task SavePreferencesCoreAsync(Func<UiPreferences, UiPreferences> update)
    {
        try
        {
            // AppearanceService serializes updates against the latest saved preferences.
            await _appearance.UpdateAsync(update);
        }
        catch (Exception exception) { SetError("OperationFailed", [exception.Message]); }
        finally
        {
            if (--_pendingPreferenceSaves == 0) _preferences = _appearance.Current;
            RefreshAppearanceProperties();
        }
    }

    private void OnAppearanceChanged(object? sender, EventArgs args)
    {
        if (_pendingPreferenceSaves == 0) _preferences = _appearance.Current;
        RefreshAppearanceProperties();
    }
    private void OnColorChanged(object? sender, EventArgs args) => OnPropertyChanged(nameof(ThemeColorStatus));
    private void RefreshAppearanceProperties()
    {
        foreach (var name in new[] { nameof(IsThemeSystem), nameof(IsThemeLight), nameof(IsThemeDark), nameof(IsLanguageSystem), nameof(IsLanguageChinese), nameof(IsLanguageEnglish), nameof(ReduceMotion), nameof(UseArtworkColors), nameof(ThemeColorStatus), nameof(PluginSummary), nameof(PluginDescriptions) }) OnPropertyChanged(name);
        foreach (var option in ThemeColors) option.Refresh();
    }
    protected override void RefreshLocalizedProperties() { base.RefreshLocalizedProperties(); RefreshAppearanceProperties(); }
    public override async ValueTask DisposeAsync()
    {
        _closing = true;
        await Task.WhenAll(_preferenceTasks);
        _appearance.Changed -= OnAppearanceChanged;
        _appearance.Colors.Changed -= OnColorChanged;
        await base.DisposeAsync();
    }
}
