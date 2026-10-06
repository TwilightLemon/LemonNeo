using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace LemonNeo.Desktop.Infrastructure;

/// <summary>Combines brightness, the chosen fallback, and the latest playback artwork color.</summary>
public sealed class ThemeColorService : IDisposable
{
    private readonly Avalonia.Application _application;
    private readonly ThemeResources _resources;
    private readonly FluentTheme? _fluent;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private IReadOnlyDictionary<string, Color> _from;
    private IReadOnlyDictionary<string, Color> _target;
    private AppearancePreferences _preferences = new();
    private Color? _artworkColor;
    private bool _applyingPreferences;
    private bool _disposed;

    public ThemeColorService(Avalonia.Application application)
    {
        _application = application;
        _resources = application.Resources.MergedDictionaries.OfType<ThemeResources>().Single();
        _fluent = application.Styles.OfType<FluentTheme>().FirstOrDefault();
        _from = _target = CreatePalette();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnTick);
        _timer.Stop();
        ApplyFrame(_target);
        application.ActualThemeVariantChanged += OnThemeChanged;
    }

    public bool IsUsingArtwork => _preferences.UseArtworkColors && _artworkColor.HasValue;
    public event EventHandler? Changed;

    public void ApplyPreferences(AppearancePreferences preferences, bool animate)
    {
        if (_disposed) return;
        _preferences = preferences;
        _applyingPreferences = true;
        try
        {
            _application.RequestedThemeVariant = preferences.Theme switch
            {
                ThemePreference.Light => ThemeVariant.Light,
                ThemePreference.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }
        finally { _applyingPreferences = false; }
        Refresh(animate);
    }

    public void SetArtworkColor(Color? color)
    {
        if (_disposed || _artworkColor == color) return;
        _artworkColor = color;
        if (_preferences.UseArtworkColors) Refresh(true);
    }

    private IReadOnlyDictionary<string, Color> CreatePalette() => ThemePalette.Create(
        IsUsingArtwork ? ThemePalette.FromArtwork(_artworkColor!.Value) : ThemePalette.Get(_preferences.ThemeColor),
        _application.ActualThemeVariant == ThemeVariant.Dark);

    private void Refresh(bool animate)
    {
        var next = CreatePalette();
        var sameTarget = next.All(p => _target[p.Key] == p.Value);
        if (!sameTarget || !animate || _preferences.ReduceMotion)
        {
            _timer.Stop();
            // Retarget from the visible frame, so quick switches cannot snap back to an old palette.
            _from = _resources.Snapshot();
            _target = next;
            if (animate && !_preferences.ReduceMotion && !sameTarget)
            {
                _clock.Restart();
                _timer.Start();
            }
            else ApplyFrame(_target);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnThemeChanged(object? sender, EventArgs args)
    {
        if (!_applyingPreferences && !_disposed) Refresh(true);
    }

    private void OnTick(object? sender, EventArgs args)
    {
        var progress = Math.Clamp(_clock.Elapsed.TotalMilliseconds / 420, 0, 1);
        var eased = progress * progress * (3 - 2 * progress);
        ApplyFrame(_target.ToDictionary(p => p.Key, p => ThemePalette.Mix(_from[p.Key], p.Value, eased)));
        if (progress >= 1) { _timer.Stop(); _clock.Stop(); }
    }

    private void ApplyFrame(IReadOnlyDictionary<string, Color> colors)
    {
        _resources.Apply(colors);
        // Fluent's Accent is its supported host palette input (checkboxes, switches, focus, etc.).
        if (_fluent is null) return;
        foreach (var palette in _fluent.Palettes.Values) palette.Accent = colors["AccentText"];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _application.ActualThemeVariantChanged -= OnThemeChanged;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
