using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Irihi.Lingua;
using LemonNeo.Configuration;
using LemonNeo.Desktop.Controls;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.Views;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class InfrastructureTests
{
    [AvaloniaFact]
    public async Task ThemeLanguageAndMotionPersistAndRestore()
    {
        using var paths = new TestPaths();
        var app = Avalonia.Application.Current!;
        var text = new LocalizationService();
        var store = new JsonAppConfigurationStore(paths);
        await store.SetAsync("other.feature", "retained");
        var appearance = new AppearanceService(app, text, store);
        await appearance.LoadAsync();
        Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
        await appearance.SetAsync(new(ThemePreference.Dark, LanguagePreference.Chinese, true));
        Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
        Assert.Equal("音乐库", text["Library"]);
        Assert.Equal(TimeSpan.Zero, app.Resources["Motion.Fast"]);
        var restored = new AppearanceService(app, text, new JsonAppConfigurationStore(paths));
        await restored.LoadAsync();
        Assert.Equal(appearance.Current, restored.Current);
        Assert.Equal("retained", await store.GetAsync<string>("other.feature"));
        await restored.SetAsync(new(ThemePreference.System, LanguagePreference.English));
        Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
        Assert.True((TimeSpan)app.Resources["Motion.Fast"]! > TimeSpan.Zero);
    }

    [AvaloniaFact]
    public async Task ExistingViewsAndDialogsUpdateWhenCultureChanges()
    {
        var text = new LocalizationService();
        var window = new MainWindow();
        var dialog = new PlaylistDialog();
        var moveDialog = new MoveTrackDialog(2, 3);
        window.Show();
        text.Apply(LanguagePreference.English);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Music library");
        Assert.Equal("New playlist", dialog.Title);
        Assert.Equal("Move to position…", moveDialog.Title);
        var switches = window.GetVisualDescendants().OfType<ToggleSwitch>().ToArray();
        Assert.NotEmpty(switches);
        Assert.All(switches, control => Assert.Equal("Off", control.OffContent));
        text.Apply(LanguagePreference.Chinese);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "音乐库");
        Assert.Equal("新建歌单", dialog.Title);
        Assert.Equal("移动到指定位置…", moveDialog.Title);
        Assert.All(switches, control => Assert.Equal("关闭", control.OffContent));
        text.Apply(LanguagePreference.English);
        window.Close();
        dialog.Close();
        moveDialog.Close();
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task PaletteSwitchUpdatesExistingControlsAndHasReadableContrast()
    {
        using var paths = new TestPaths();
        var appearance = new AppearanceService(Avalonia.Application.Current!, new LocalizationService(), new JsonAppConfigurationStore(paths));
        var window = new MainWindow();
        window.Show();
        var colors = new List<Color>();
        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
        {
            await appearance.SetAsync(new(theme, LanguagePreference.English, true));
            Dispatcher.UIThread.RunJobs();
            var canvas = ((ISolidColorBrush)window.Background!).Color;
            var foreground = ((ISolidColorBrush)window.Foreground!).Color;
            colors.Add(canvas);
            Assert.True(Contrast(canvas, foreground) >= 4.5, "Body text must meet WCAG AA contrast in both palettes.");
            Assert.True(window.TryFindResource("Brush.Accent", window.ActualThemeVariant, out var accent));
            Assert.True(window.TryFindResource("Brush.OnAccent", window.ActualThemeVariant, out var onAccent));
            Assert.True(Contrast(((ISolidColorBrush)accent!).Color, ((ISolidColorBrush)onAccent!).Color) >= 4.5);
            Assert.NotEmpty(window.GetVisualDescendants().OfType<PageHeader>());
            Assert.NotEmpty(window.GetVisualDescendants().OfType<EmptyState>());
        }
        Assert.NotEqual(colors[0], colors[1]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task FailedPreferenceWriteDoesNotChangeLiveState()
    {
        var text = new LocalizationService();
        var appearance = new AppearanceService(Avalonia.Application.Current!, text, new FailingStore());
        await appearance.LoadAsync();
        var initial = appearance.Current;
        await Assert.ThrowsAsync<IOException>(() => appearance.SetAsync(new(ThemePreference.Dark, LanguagePreference.Chinese)));
        Assert.Equal(initial, appearance.Current);
        Assert.Equal(ThemeVariant.Default, Avalonia.Application.Current!.RequestedThemeVariant);
    }

    [AvaloniaFact]
    public async Task UnknownPreferencesFallBackToSystem()
    {
        using var paths = new TestPaths();
        var store = new JsonAppConfigurationStore(paths);
        await store.SetAsync(AppearanceService.ConfigurationKey, new UiPreferences((ThemePreference)99, (LanguagePreference)99));
        var appearance = new AppearanceService(Avalonia.Application.Current!, new LocalizationService(), store);
        await appearance.LoadAsync();
        Assert.Equal(ThemePreference.System, appearance.Current.Theme);
        Assert.Equal(LanguagePreference.System, appearance.Current.Language);
    }

    [AvaloniaFact]
    public void LinguaFallsBackAndFormatsBothCatalogs()
    {
        var text = new LocalizationService();
        LanguageManager.Instance.UpdateCulture(CultureInfo.GetCultureInfo("fr-FR"));
        Assert.Equal("Music library", ((LinguaObservableString)LanguageManager.Instance.Library).CurrentValue);
        text.Apply(LanguagePreference.Chinese);
        Assert.Equal("2 个歌单 · 3 首歌曲", text.Format("PlaylistCount", 2, 3));
        text.Apply(LanguagePreference.English);
        Assert.Equal("2 playlists · 3 tracks", text.Format("PlaylistCount", 2, 3));
    }

    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte v) { var x = v / 255d; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        var first = Luminance(a); var second = Luminance(b);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private sealed class FailingStore : IAppConfigurationStore
    {
        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) => Task.FromResult(default(T));
        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) => throw new IOException("Simulated read-only settings directory.");
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
