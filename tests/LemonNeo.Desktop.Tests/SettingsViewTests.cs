using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.ViewModels;
using LemonNeo.Desktop.Views;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class SettingsViewTests
{
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
}
