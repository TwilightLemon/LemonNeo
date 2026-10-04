using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using LemonNeo.Configuration;
using LemonNeo.Desktop.Infrastructure;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class AppearanceTests
{
    [AvaloniaFact]
    public async Task ArtworkOverrideCanBeDisabledAndFallsBackToTheSavedPalette()
    {
        var app = Avalonia.Application.Current!;
        var resources = app.Resources.MergedDictionaries.OfType<ThemeResources>().Single();
        using var appearance = new AppearanceService(app, new LocalizationService(), new MemoryStore());
        var preferences = new UiPreferences(Theme: ThemePreference.Dark, ReduceMotion: true,
            ThemeColor: ThemeColorPreset.PolarRose, UseArtworkColors: true);
        await appearance.SetAsync(preferences);
        var fallback = resources.Snapshot()["Accent"];
        appearance.SetArtworkColor(Colors.Teal);
        Assert.True(appearance.Colors.IsUsingArtwork);
        Assert.NotEqual(fallback, resources.Snapshot()["Accent"]);
        // Choosing a fallback while artwork is active must not replace the artwork palette.
        var artworkAccent = resources.Snapshot()["Accent"];
        await appearance.UpdateAsync(p => p with { ThemeColor = ThemeColorPreset.MoonGold });
        Assert.Equal(artworkAccent, resources.Snapshot()["Accent"]);
        await appearance.UpdateAsync(p => p with { UseArtworkColors = false });
        Assert.False(appearance.Colors.IsUsingArtwork);
        var gold = resources.Snapshot()["Accent"];
        Assert.NotEqual(artworkAccent, gold);
        await appearance.UpdateAsync(p => p with { UseArtworkColors = true });
        Assert.Equal(artworkAccent, resources.Snapshot()["Accent"]);
        appearance.SetArtworkColor(null);
        Assert.False(appearance.Colors.IsUsingArtwork);
        Assert.Equal(gold, resources.Snapshot()["Accent"]);
    }

    [AvaloniaFact]
    public async Task PreferencesRoundTripAndOldSettingsKeepTheDefaultPalette()
    {
        var store = new MemoryStore();
        using (var appearance = new AppearanceService(Avalonia.Application.Current!, new LocalizationService(), store))
            await appearance.SetAsync(new UiPreferences(Theme: ThemePreference.Light, ThemeColor: ThemeColorPreset.Twilight, UseArtworkColors: true));
        using (var reloaded = new AppearanceService(Avalonia.Application.Current!, new LocalizationService(), store))
        {
            await reloaded.LoadAsync();
            Assert.Equal(ThemeColorPreset.Twilight, reloaded.Current.ThemeColor);
            Assert.True(reloaded.Current.UseArtworkColors);
            Assert.Equal(ThemePreference.Light, reloaded.Current.Theme);
        }
        store.Json = "{\"Theme\":2,\"ReduceMotion\":true}";
        using var legacy = new AppearanceService(Avalonia.Application.Current!, new LocalizationService(), store);
        await legacy.LoadAsync();
        Assert.Equal(ThemeColorPreset.Classic, legacy.Current.ThemeColor);
        Assert.False(legacy.Current.UseArtworkColors);
        Assert.Equal(ThemePreference.Dark, legacy.Current.Theme);
    }

    [AvaloniaFact]
    public async Task FailedSaveDoesNotChangeTheVisiblePalette()
    {
        var app = Avalonia.Application.Current!;
        var store = new MemoryStore();
        using var appearance = new AppearanceService(app, new LocalizationService(), store);
        var before = app.Resources.MergedDictionaries.OfType<ThemeResources>().Single().Snapshot();
        store.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => appearance.SetAsync(new UiPreferences(ThemeColor: ThemeColorPreset.PolarRose)));
        Assert.Equal(ThemeColorPreset.Classic, appearance.Current.ThemeColor);
        Assert.Equal(before, app.Resources.MergedDictionaries.OfType<ThemeResources>().Single().Snapshot());
    }

    [AvaloniaFact]
    public async Task AnimatedSwitchKeepsBrushesAndRetargetsFromTheVisibleColor()
    {
        var app = Avalonia.Application.Current!;
        using var appearance = new AppearanceService(app, new LocalizationService(), new MemoryStore());
        await appearance.SetAsync(new UiPreferences(Theme: ThemePreference.Dark, ReduceMotion: true));
        var resources = app.Resources.MergedDictionaries.OfType<ThemeResources>().Single();
        var brush = (SolidColorBrush)resources["Brush.Accent"]!;
        var initial = brush.Color;
        var label = new TextBlock();
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("Brush.Accent"));
        var window = new Window { Content = label };
        window.Show();
        try
        {
            await appearance.UpdateAsync(p => p with { ThemeColor = ThemeColorPreset.PolarRose, ReduceMotion = false });
            Assert.Equal(initial, brush.Color);
            await Task.Delay(110);
            var intermediate = brush.Color;
            Assert.NotEqual(initial, intermediate);
            Assert.NotEqual(ThemePalette.Create(ThemePalette.Get(ThemeColorPreset.PolarRose), true)["Accent"], intermediate);
            await appearance.UpdateAsync(p => p with { ThemeColor = ThemeColorPreset.Aurora });
            Assert.Equal(intermediate, brush.Color);
            await Task.Delay(550);
            Assert.Equal(ThemePalette.Create(ThemePalette.Get(ThemeColorPreset.Aurora), true)["Accent"], brush.Color);
            Assert.Same(brush, resources["Brush.Accent"]);
            Assert.Same(brush, label.Foreground);
            Assert.Equal(resources.Snapshot()["AccentText"], app.Styles.OfType<FluentTheme>().Single().Palettes[ThemeVariant.Dark].Accent);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ReduceMotionCompletesAnInFlightTransitionImmediately()
    {
        var app = Avalonia.Application.Current!;
        using var appearance = new AppearanceService(app, new LocalizationService(), new MemoryStore());
        await appearance.SetAsync(new UiPreferences(Theme: ThemePreference.Light, ThemeColor: ThemeColorPreset.IceCyan));
        await appearance.UpdateAsync(p => p with { ReduceMotion = true });
        var expected = ThemePalette.Create(ThemePalette.Get(ThemeColorPreset.IceCyan), false)["Accent"];
        var resources = app.Resources.MergedDictionaries.OfType<ThemeResources>().Single();
        Assert.Equal(expected, resources.Snapshot()["Accent"]);
        await Task.Delay(100);
        Assert.Equal(expected, resources.Snapshot()["Accent"]);
    }

    internal sealed class MemoryStore : IAppConfigurationStore
    {
        public string? Json { get; set; }
        public bool FailWrites { get; set; }
        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) => Task.FromResult(Json is null ? default : JsonSerializer.Deserialize<T>(Json));
        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        {
            if (FailWrites) throw new IOException("Test write failure");
            Json = JsonSerializer.Serialize(value);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) { Json = null; return Task.CompletedTask; }
    }
}
