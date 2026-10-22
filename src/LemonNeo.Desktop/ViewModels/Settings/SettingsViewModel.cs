using CommunityToolkit.Mvvm.Input;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Application.Playback;
using LemonNeo.Core.Playback;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.Utils;
using LemonNeo.Desktop.ViewModels.Shared;
using LemonNeo.Hosting;

namespace LemonNeo.Desktop.ViewModels.Settings;

public sealed class SettingsViewModel : PageViewModel
{
    private static readonly int[] MeshFrameRates = [-1, 30, 60, 120, 144, 240];
    private readonly IDisposable _appearanceSubscription;
    private readonly IDisposable _languageSubscription;
    private readonly ISettingsScope _playbackSettings;
    private readonly IDisposable _playbackSubscription;
    private readonly ShortcutService? _shortcuts;
    private readonly HostStorageLocations _storage;
    private readonly IDesktopStorageService _storageService;
    private readonly IDisposable _visualEffectsSubscription;
    private bool _closing;

    public SettingsViewModel(DesktopContext context) : base(context, DesktopRoutes.Settings)
    {
        _storage = _host.StorageLocations;
        _storageService = _host.Get<IDesktopStorageService>();
        _storage.Changed += OnStorageChanged;
        _playbackSettings = _host.Settings.ForPackage(PackageNames.Application);
        _playbackSubscription =
            _playbackSettings.Subscribe<PlaybackPreferences>(_ => OnPropertyChanged(nameof(SelectedAudioQuality)));
        ThemeCommand = new RelayCommand<string>(value =>
            ApplyAppearance(current => current with { Theme = Enum.Parse<ThemePreference>(value!) }));
        LanguageCommand = new RelayCommand<string>(value =>
        {
            if (!_closing)
                Context.Settings.Apply<LanguagePreferences>(current =>
                    current with { Language = Enum.Parse<LanguagePreference>(value!) });
        });
        RestoreVisualEffectsDefaultsCommand = new RelayCommand(() =>
            ApplyVisualEffects(_ => new VisualEffectsPreferences()));
        ThemeColors = ThemePalette.BuiltIn.Select(value => new ThemeColorOption(value, _appearance, _text,
            id => ApplyAppearance(current => current with { ThemeColor = id }))).ToArray();
        _appearanceSubscription = Context.Settings.Subscribe<AppearancePreferences>(_ => RefreshAppearanceProperties());
        _languageSubscription = Context.Settings.Subscribe<LanguagePreferences>(_ => RefreshLanguageProperties());
        _visualEffectsSubscription =
            Context.Settings.Subscribe<VisualEffectsPreferences>(_ => RefreshVisualEffectsProperties());
        _appearance.Colors.Changed += OnColorChanged;
        _shortcuts = Context.Shortcuts;
        if (_shortcuts is not null) _shortcuts.Changed += OnShortcutsChanged;
        ShortcutRows = BuildShortcutRows();
        ResetAllShortcutsCommand = new RelayCommand(() => _shortcuts?.ResetAll());
    }

    private AppearanceService _appearance => Context.Appearance;

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
    public RelayCommand RestoreVisualEffectsDefaultsCommand { get; }
    public RelayCommand ResetAllShortcutsCommand { get; }
    public IReadOnlyList<ThemeColorOption> ThemeColors { get; }
    public IReadOnlyList<ShortcutRowViewModel> ShortcutRows { get; private set; }

    private VisualEffectsPreferences VisualEffects => Context.Settings.Get<VisualEffectsPreferences>();

    public bool AudioVisualizerEnabled
    {
        get => VisualEffects.AudioVisualizerEnabled;
        set => ApplyVisualEffects(current => current with { AudioVisualizerEnabled = value });
    }

    public double AudioVisualizerStripCount
    {
        get => VisualEffects.AudioVisualizerStripCount;
        set
        {
            if (double.IsFinite(value))
                ApplyVisualEffects(current => current with { AudioVisualizerStripCount = (int)Math.Round(value) });
        }
    }

    public bool AppleMusicMeshEnabled
    {
        get => VisualEffects.AppleMusicMeshEnabled;
        set => ApplyVisualEffects(current => current with { AppleMusicMeshEnabled = value });
    }

    public bool AppleMusicMeshLyricsOnly
    {
        get => VisualEffects.AppleMusicMeshLyricsOnly;
        set => ApplyVisualEffects(current => current with { AppleMusicMeshLyricsOnly = value });
    }

    public IReadOnlyList<string> MeshPresetOptions =>
    [
        _text[LanguageManager.Keys.MeshPresetRandom],
        .. Enumerable.Range(1, 5).Select(index => _text.Format(LanguageManager.Keys.MeshPreset, index))
    ];

    public int SelectedMeshPresetIndex
    {
        get => VisualEffects.AppleMusicMeshPresetSlot + 1;
        set
        {
            if (value is >= 0 and <= 5)
                ApplyVisualEffects(current => current with { AppleMusicMeshPresetSlot = value - 1 });
        }
    }

    public IReadOnlyList<string> MeshFrameRateOptions =>
    [
        _text[LanguageManager.Keys.MeshFrameRateDisplay],
        .. MeshFrameRates.Skip(1).Select(value => _text.Format(LanguageManager.Keys.MeshFrameRateFps, value))
    ];

    public int SelectedMeshFrameRateIndex
    {
        get
        {
            var index = Array.IndexOf(MeshFrameRates, VisualEffects.AppleMusicMeshFrameRateLimit);
            return index < 0 ? 3 : index;
        }
        set
        {
            if (value >= 0 && value < MeshFrameRates.Length)
                ApplyVisualEffects(current => current with { AppleMusicMeshFrameRateLimit = MeshFrameRates[value] });
        }
    }

    public double MeshRenderScale
    {
        get => VisualEffects.AppleMusicMeshRenderScale;
        set => ApplyFiniteVisualEffect(value,
            (current, next) => current with { AppleMusicMeshRenderScale = next });
    }

    public double MeshRotationScale
    {
        get => VisualEffects.AppleMusicMeshRotationScale;
        set => ApplyFiniteVisualEffect(value,
            (current, next) => current with { AppleMusicMeshRotationScale = next });
    }

    public double MeshBassPulseScale
    {
        get => VisualEffects.AppleMusicMeshBassPulseScale;
        set => ApplyFiniteVisualEffect(value,
            (current, next) => current with { AppleMusicMeshBassPulseScale = next });
    }

    public double MeshBlurScale
    {
        get => VisualEffects.AppleMusicMeshBlurScale;
        set => ApplyFiniteVisualEffect(value,
            (current, next) => current with { AppleMusicMeshBlurScale = next });
    }

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
        ? _text[LanguageManager.Keys.ThemeColorUsingArtwork]
        : _text.Format(UseArtworkColors
                ? LanguageManager.Keys.ThemeColorFallbackStatus
                : LanguageManager.Keys.ThemeColorBuiltInStatus,
            _text[ThemePalette.Get(_appearance.Current.ThemeColor).NameKey]);

    public IEnumerable<string> PluginDescriptions =>
        _host.Plugins.Loaded.Select(value => $"{value.Manifest.Name}  ·  {value.Manifest.Version}");

    public bool IsThemeSystem => _appearance.Current.Theme == ThemePreference.System;
    public bool IsThemeLight => _appearance.Current.Theme == ThemePreference.Light;
    public bool IsThemeDark => _appearance.Current.Theme == ThemePreference.Dark;
    public bool IsLanguageSystem => Context.Settings.Get<LanguagePreferences>().Language == LanguagePreference.System;
    public bool IsLanguageChinese => Context.Settings.Get<LanguagePreferences>().Language == LanguagePreference.Chinese;
    public bool IsLanguageEnglish => Context.Settings.Get<LanguagePreferences>().Language == LanguagePreference.English;
    public string PluginSummary => _text.Format(LanguageManager.Keys.PluginCount, _host.Plugins.Loaded.Count);
    public string LibraryPath => _storage.LibraryDirectory;
    public string ConfigurationPath => _host.Paths.ConfigurationDirectory;
    public string CachePath => _storage.CacheDirectory;

    public Task ChangeLibraryAsync(string directory)
    {
        return ExecuteAsync(ct => _storageService.ChangeLibraryAsync(directory, ct));
    }

    public Task ChangeCacheAsync(string directory)
    {
        return ExecuteAsync(ct => _storageService.ChangeCacheAsync(directory, ct));
    }

    private void OnStorageChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(LibraryPath));
        OnPropertyChanged(nameof(CachePath));
    }

    private void ApplyAppearance(Func<AppearancePreferences, AppearancePreferences> update)
    {
        if (!_closing) Context.Settings.Apply(update);
    }

    private void ApplyVisualEffects(Func<VisualEffectsPreferences, VisualEffectsPreferences> update)
    {
        if (!_closing) Context.Settings.Apply(update);
    }

    private void ApplyFiniteVisualEffect(double value,
        Func<VisualEffectsPreferences, double, VisualEffectsPreferences> update)
    {
        if (double.IsFinite(value)) ApplyVisualEffects(current => update(current, value));
    }

    private void OnColorChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(ThemeColorStatus));
    }

    private IReadOnlyList<ShortcutRowViewModel> BuildShortcutRows()
    {
        return _shortcuts is null
            ? []
            : _shortcuts.Actions
                .Select(action => new ShortcutRowViewModel(_shortcuts, _text, action))
                .ToArray();
    }

    private void OnShortcutsChanged(object? sender, EventArgs args)
    {
        // Rebuilding keeps gesture text, default state, and conflict flags consistent across rows.
        ShortcutRows = BuildShortcutRows();
        OnPropertyChanged(nameof(ShortcutRows));
    }

    private void RefreshLanguageProperties()
    {
        foreach (var name in new[] { nameof(IsLanguageSystem), nameof(IsLanguageChinese), nameof(IsLanguageEnglish) })
            OnPropertyChanged(name);
    }

    private void RefreshAppearanceProperties()
    {
        foreach (var name in new[]
                 {
                     nameof(IsThemeSystem), nameof(IsThemeLight), nameof(IsThemeDark), nameof(ReduceMotion),
                     nameof(UseArtworkColors), nameof(ThemeColorStatus)
                 }) OnPropertyChanged(name);
        foreach (var option in ThemeColors) option.Refresh();
    }

    private void RefreshVisualEffectsProperties()
    {
        foreach (var name in new[]
                 {
                     nameof(AudioVisualizerEnabled), nameof(AudioVisualizerStripCount),
                     nameof(AppleMusicMeshEnabled), nameof(AppleMusicMeshLyricsOnly),
                     nameof(SelectedMeshPresetIndex), nameof(SelectedMeshFrameRateIndex),
                     nameof(MeshRenderScale), nameof(MeshRotationScale), nameof(MeshBassPulseScale),
                     nameof(MeshBlurScale)
                 }) OnPropertyChanged(name);
    }

    protected override void RefreshLocalizedProperties()
    {
        base.RefreshLocalizedProperties();
        RefreshAppearanceProperties();
        RefreshLanguageProperties();
        OnPropertyChanged(nameof(AudioQualityOptions));
        OnPropertyChanged(nameof(SelectedAudioQuality));
        OnPropertyChanged(nameof(MeshPresetOptions));
        OnPropertyChanged(nameof(MeshFrameRateOptions));
        OnPropertyChanged(nameof(PluginSummary));
        OnPropertyChanged(nameof(PluginDescriptions));
        OnShortcutsChanged(this, EventArgs.Empty);
    }

    public override async ValueTask DisposeAsync()
    {
        _closing = true;
        _storage.Changed -= OnStorageChanged;
        _playbackSubscription.Dispose();
        _appearanceSubscription.Dispose();
        _languageSubscription.Dispose();
        _visualEffectsSubscription.Dispose();
        _appearance.Colors.Changed -= OnColorChanged;
        if (_shortcuts is not null) _shortcuts.Changed -= OnShortcutsChanged;
        await base.DisposeAsync();
    }
}
