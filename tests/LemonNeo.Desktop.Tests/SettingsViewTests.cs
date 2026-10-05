using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Application;
using LemonNeo.Configuration;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.ViewModels;
using LemonNeo.Desktop.Views;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class SettingsViewTests
{
    [AvaloniaFact]
    public async Task SlowSavesStaySilentAndShutdownFlushesEveryQueuedChange()
    {
        using var paths = new TestPaths();
        await using var runtime = new DesktopRuntime(paths,
            new DesktopOptions(Path.Combine(paths.DataDirectory, "plugins"), paths.MusicLibraryDirectory));
        var store = new DelayedStore();
        await store.SetAsync(AppearanceService.ConfigurationKey,
            new UiPreferences(Language: LanguagePreference.Chinese, ReduceMotion: true));
        var text = new LocalizationService();
        using var appearance = new AppearanceService(Avalonia.Application.Current!, text, store);
        var shell = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        var window = new MainWindow { DataContext = shell };
        Task? shutdown = null;
        try
        {
            await shell.InitializeAsync();
            window.Show();
            await shell.Navigation.NavigateAsync(DesktopRoutes.Settings);
            var settings = shell.Settings;
            var busyChanges = 0;
            settings.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(settings.IsBusy)) busyChanges++;
            };
            store.DelayWrites = true;
            var dark = settings.ThemeCommand.ExecuteAsync("Dark");
            await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.True(settings.ThemeCommand.CanExecute("Light"));
            var light = settings.ThemeCommand.ExecuteAsync("Light");
            var language = settings.LanguageCommand.ExecuteAsync("English");
            settings.UseArtworkColors = true;
            settings.UseArtworkColors = false;
            settings.ReduceMotion = false;
            settings.ReduceMotion = true;
            var color = settings.ThemeColors[1].SelectCommand.ExecuteAsync(null);

            Dispatcher.UIThread.RunJobs();
            Assert.False(settings.IsBusy);
            Assert.False(shell.IsBusy);
            Assert.False(settings.HasStatus);
            Assert.True(settings.CanInteract);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<ProgressBar>(), progress => progress.IsEffectivelyVisible);
            var view = window.GetVisualDescendants().OfType<SettingsView>().Single();
            Assert.All(view.GetVisualDescendants().OfType<ToggleSwitch>(), toggle => Assert.True(toggle.IsEffectivelyEnabled));

            shutdown = shell.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);
            store.Release.TrySetResult();
            await Task.WhenAll(dark, light, language, color, shutdown);
            var saved = await store.GetAsync<UiPreferences>(AppearanceService.ConfigurationKey);
            Assert.NotNull(saved);
            Assert.Equal(appearance.Current, saved);
            Assert.Equal(ThemePreference.Light, saved.Theme);
            Assert.Equal(LanguagePreference.English, saved.Language);
            Assert.False(saved.UseArtworkColors);
            Assert.True(saved.ReduceMotion);
            Assert.Equal(ThemePalette.BuiltIn[1].Id, saved.ThemeColor);
            Assert.Equal(0, busyChanges);
            Assert.False(settings.HasError, settings.Error);
        }
        finally
        {
            store.Release.TrySetResult();
            await (shutdown ?? shell.DisposeAsync().AsTask());
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task FailedSilentSaveShowsAnErrorAndCanBeRetried()
    {
        using var paths = new TestPaths();
        await using var runtime = new DesktopRuntime(paths,
            new DesktopOptions(Path.Combine(paths.DataDirectory, "plugins"), paths.MusicLibraryDirectory));
        var store = new AppearanceTests.MemoryStore { FailWrites = true };
        var text = new LocalizationService();
        using var appearance = new AppearanceService(Avalonia.Application.Current!, text, store);
        await using var settings = new SettingsViewModel(new DesktopContext(runtime, text, appearance));
        await settings.ThemeCommand.ExecuteAsync("Dark");
        Assert.True(settings.HasError);
        Assert.Contains("Test write failure", settings.Error);
        Assert.False(settings.IsBusy);
        Assert.False(settings.HasStatus);
        Assert.Equal(ThemePreference.System, appearance.Current.Theme);

        store.FailWrites = false;
        await settings.ThemeCommand.ExecuteAsync("Dark");
        Assert.False(settings.HasError);
        Assert.Equal(ThemePreference.Dark, appearance.Current.Theme);
        Assert.Equal(appearance.Current, await store.GetAsync<UiPreferences>(AppearanceService.ConfigurationKey));
    }

    [AvaloniaFact]
    public async Task PaletteChoicesRenderAndPersistInBothModes()
    {
        using var paths = new TestPaths();
        await using var runtime = new DesktopRuntime(paths, new DesktopOptions(Path.Combine(paths.DataDirectory, "plugins"), paths.MusicLibraryDirectory));
        var text = new LocalizationService();
        using var appearance = new AppearanceService(Avalonia.Application.Current!, text, new AppearanceTests.MemoryStore());
        await appearance.SetAsync(new UiPreferences(Language: LanguagePreference.Chinese, ReduceMotion: true));
        var context = new DesktopContext(runtime, text, appearance);
        await using var viewModel = new SettingsViewModel(context);
        var view = new SettingsView { DataContext = viewModel };
        var window = new Window { Width = 1050, Height = 940, Content = new Border { Padding = new Avalonia.Thickness(28), Child = view } };
        window.Show();
        try
        {
            foreach (var mode in new[] { ThemePreference.Light, ThemePreference.Dark })
            foreach (var (definition, option) in ThemePalette.BuiltIn.Zip(viewModel.ThemeColors))
            {
                await viewModel.ThemeCommand.ExecuteAsync(mode.ToString());
                await option.SelectCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();
                Assert.False(viewModel.HasError, viewModel.Error);
                Assert.Equal(definition.Id, appearance.Current.ThemeColor);
                Assert.True(option.IsSelected);
                Assert.Single(viewModel.ThemeColors, p => p.IsSelected);
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == option.Name);
                var previewDirectory = Environment.GetEnvironmentVariable("LEMONNEO_THEME_PREVIEW_DIR");
                if (!string.IsNullOrEmpty(previewDirectory))
                {
                    Directory.CreateDirectory(previewDirectory);
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    frame.Save(Path.Combine(previewDirectory, $"{definition.Id}-{mode}.png"), PngBitmapEncoderOptions.Default);
                }
            }
            viewModel.UseArtworkColors = true;
            Assert.True(appearance.Current.UseArtworkColors);
            Assert.Contains(text[ThemePalette.Get(appearance.Current.ThemeColor).NameKey], viewModel.ThemeColorStatus);
        }
        finally { window.Close(); }
    }

    private sealed class DelayedStore : IAppConfigurationStore
    {
        private readonly AppearanceTests.MemoryStore _store = new();
        public bool DelayWrites { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) => _store.GetAsync<T>(key, cancellationToken);
        public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        {
            if (DelayWrites)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            await _store.SetAsync(key, value, cancellationToken);
        }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => _store.DeleteAsync(key, cancellationToken);
    }
}
