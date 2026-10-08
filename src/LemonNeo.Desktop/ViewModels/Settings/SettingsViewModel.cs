using LemonNeo.Abstractions.Settings;
using LemonNeo.Application.Playback;
using LemonNeo.Core.Playback;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.Utils;
using LemonNeo.Desktop.ViewModels.Shared;
using LemonNeo.Hosting;

namespace LemonNeo.Desktop.ViewModels.Settings;

public sealed class SettingsViewModel : PageViewModel
{
    private AppearanceService _appearance => Context.Appearance;
    private readonly IDisposable _appearanceSubscription;
    private readonly IDisposable _languageSubscription;
    private readonly ISettingsScope _playbackSettings;
    private readonly IDisposable _playbackSubscription;
    private readonly HostStorageLocations _storage;
    private readonly IDesktopStorageService _storageService;
    private bool _closing;

    public SettingsViewModel(DesktopContext context) : base(context, DesktopRoutes.Settings)
    {
        _storage = _host.StorageLocations;
        _storageService = _host.Get<IDesktopStorageService>();
        _storage.Changed += OnStorageChanged;
        _playbackSettings = _host.Settings.ForPackage(PackageNames.Application);
        _playbackSubscription = _playbackSettings.Subscribe<PlaybackPreferences>(_ => OnPropertyChanged(nameof(SelectedAudioQuality)));
        ThemeCommand = new(value => ApplyAppearance(current => current with { Theme = Enum.Parse<ThemePreference>(value!) }));
        LanguageCommand = new(value =>
        {
            if (!_closing) Context.Settings.Apply<LanguagePreferences>(current =>
                current with { Language = Enum.Parse<LanguagePreference>(value!) });
        });
        ThemeColors = ThemePalette.BuiltIn.Select(value => new ThemeColorOption(value, _appearance, _text,
            id => ApplyAppearance(current => current with { ThemeColor = id }))).ToArray();
        _appearanceSubscription = Context.Settings.Subscribe<AppearancePreferences>(_ => RefreshAppearanceProperties());
        _languageSubscription = Context.Settings.Subscribe<LanguagePreferences>(_ => RefreshLanguageProperties());
        _appearance.Colors.Changed += OnColorChanged;
    }

    public IReadOnlyList<string> AudioQualityOptions => Enum.GetValues<AudioQuality>()
        .Select(quality => _text["AudioQuality" + quality]).ToArray();
    public int SelectedAudioQuality
    {
        get => (int)_playbackSettings.Get<PlaybackPreferences>().Quality;
        set
        {
            if (!_closing && Enum.IsDefined((AudioQuality)value))
                _playbackSettings.Apply<PlaybackPreferences>(current => current with { Quality = (AudioQuality)value });
        }
    }

    public RelayCommand<string> ThemeCommand { get; }
    public RelayCommand<string> LanguageCommand { get; }
    public IReadOnlyList<ThemeColorOption> ThemeColors { get; }
    public bool UseArtworkColors
    {
        get => _appearance.Current.UseArtworkColors;
        set => ApplyAppearance(current => current with { UseArtworkColors = value });
    }
    public bool ReduceMotion
    {
        get => _appearance.Current.ReduceMotion;
        set => ApplyAppearance(current => current with { ReduceMotion = value });
    }
    public string ThemeColorStatus => _appearance.Colors.IsUsingArtwork
        ? _text["ThemeColorUsingArtwork"]
        : _text.Format(UseArtworkColors ? "ThemeColorFallbackStatus" : "ThemeColorBuiltInStatus", _text[ThemePalette.Get(_appearance.Current.ThemeColor).NameKey]);
    public IEnumerable<string> PluginDescriptions => _host.Plugins.Loaded.Select(value => $"{value.Manifest.Name}  ·  {value.Manifest.Version}");
    public bool IsThemeSystem => _appearance.Current.Theme == ThemePreference.System;
    public bool IsThemeLight => _appearance.Current.Theme == ThemePreference.Light;
    public bool IsThemeDark => _appearance.Current.Theme == ThemePreference.Dark;
    public bool IsLanguageSystem => Context.Settings.Get<LanguagePreferences>().Language == LanguagePreference.System;
    public bool IsLanguageChinese => Context.Settings.Get<LanguagePreferences>().Language == LanguagePreference.Chinese;
    public bool IsLanguageEnglish => Context.Settings.Get<LanguagePreferences>().Language == LanguagePreference.English;
    public string PluginSummary => _text.Format("PluginCount", _host.Plugins.Loaded.Count);
    public string LibraryPath => _storage.LibraryDirectory;
    public string ConfigurationPath => _host.Paths.ConfigurationDirectory;
    public string CachePath => _storage.CacheDirectory;

    public Task ChangeLibraryAsync(string directory) =>
        ExecuteAsync(ct => _storageService.ChangeLibraryAsync(directory, ct));

    public Task ChangeCacheAsync(string directory) =>
        ExecuteAsync(ct => _storageService.ChangeCacheAsync(directory, ct));

    private void OnStorageChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(LibraryPath));
        OnPropertyChanged(nameof(CachePath));
    }

    private void ApplyAppearance(Func<AppearancePreferences, AppearancePreferences> update)
    {
        if (!_closing) Context.Settings.Apply(update);
    }
    private void OnColorChanged(object? sender, EventArgs args) => OnPropertyChanged(nameof(ThemeColorStatus));
    private void RefreshLanguageProperties()
    {
        foreach (var name in new[] { nameof(IsLanguageSystem), nameof(IsLanguageChinese), nameof(IsLanguageEnglish) })
            OnPropertyChanged(name);
    }
    private void RefreshAppearanceProperties()
    {
        foreach (var name in new[] { nameof(IsThemeSystem), nameof(IsThemeLight), nameof(IsThemeDark), nameof(ReduceMotion),
                     nameof(UseArtworkColors), nameof(ThemeColorStatus) }) OnPropertyChanged(name);
        foreach (var option in ThemeColors) option.Refresh();
    }
    protected override void RefreshLocalizedProperties()
    {
        base.RefreshLocalizedProperties();
        RefreshAppearanceProperties();
        RefreshLanguageProperties();
        OnPropertyChanged(nameof(AudioQualityOptions));
        OnPropertyChanged(nameof(SelectedAudioQuality));
        OnPropertyChanged(nameof(PluginSummary));
        OnPropertyChanged(nameof(PluginDescriptions));
    }
    public override async ValueTask DisposeAsync()
    {
        _closing = true;
        _storage.Changed -= OnStorageChanged;
        _playbackSubscription.Dispose();
        _appearanceSubscription.Dispose();
        _languageSubscription.Dispose();
        _appearance.Colors.Changed -= OnColorChanged;
        await base.DisposeAsync();
    }
}
