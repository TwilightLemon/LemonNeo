using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Application;
using LemonNeo.Desktop.Controls;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.ViewModels;
using LemonNeo.Desktop.Views;
using LemonNeo.Plugin.Abstractions;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(LemonNeo.Runtime.Tests.HeadlessApplication))]

namespace LemonNeo.Runtime.Tests;

public static class HeadlessApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<LemonNeo.Desktop.App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class DesktopConfigurationTests
{
    [AvaloniaFact]
    public async Task DesktopLyricsFontChangesKeepPlaylistRowsSelectionAndLoadedArtwork()
    {
        using var paths = new TemporaryPaths();
        var imageFile = Path.Combine(paths.Root, "cover.png");
        using (var bitmap = new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque))
        {
            using (var pixels = bitmap.Lock()) Marshal.Copy(Enumerable.Repeat((byte)255, pixels.RowBytes * 2).ToArray(), 0, pixels.Address, pixels.RowBytes * 2);
            bitmap.Save(imageFile, PngBitmapEncoderOptions.Default);
        }
        var uri = new Uri(imageFile);
        await using var runtime = new DesktopRuntime(paths, new(paths.PluginDirectory, paths.MusicLibraryDirectory), [new TestPlayer()], [new TestMusic()]);
        var scope = runtime.Settings.ForPackage(PackageNames.Desktop);
        DesktopPreferences.Register(scope);
        using var text = new LocalizationService(scope);
        using var appearance = new AppearanceService(Avalonia.Application.Current!, scope);
        await using var main = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        await main.InitializeAsync();
        Assert.True(main.SharedContext.IsInitialized);
        await runtime.Settings.StopAsync();
        var playlists = runtime.Get<PlaylistService>();
        var playlist = playlists.Create("Acceptance playlist");
        playlists.Add(playlist.Id, RuntimeFixture.Track() with { ArtworkUri = uri });
        main.SharedContext.RefreshLibrary();
        var row = Assert.Single(main.Library.Tracks);
        main.Library.SelectedTrack = row;
        var window = new MainWindow { DataContext = main };
        window.Show();
        try
        {
            window.Measure(new Size(1280, 800));
            window.Arrange(new Rect(0, 0, 1280, 800));
            Dispatcher.UIThread.RunJobs();
            var artwork = Assert.Single(window.GetVisualDescendants().OfType<Artwork>(), control => control.SourceUri == uri);
            if (artwork.Source is null)
            {
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnLoaded(object? sender, AvaloniaPropertyChangedEventArgs args)
                {
                    if (args.Property == Artwork.SourceProperty && artwork.Source is not null) loaded.TrySetResult();
                }
                artwork.PropertyChanged += OnLoaded;
                try { await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken); }
                finally { artwork.PropertyChanged -= OnLoaded; }
            }
            var source = artwork.Source;
            Assert.NotNull(source);
            var listChanges = 0;
            var sourceChanges = 0;
            var languageChanges = 0;
            main.Library.Tracks.CollectionChanged += (_, _) => listChanges++;
            artwork.PropertyChanged += (_, args) => { if (args.Property == Artwork.SourceProperty) sourceChanges++; };
            text.Changed += (_, _) => languageChanges++;
            for (var size = 16; size <= 48; size++) main.Lyrics.DesktopLyricFontSize = size;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(row, Assert.Single(main.Library.Tracks));
            Assert.Same(row, main.Library.SelectedTrack);
            Assert.Same(artwork, Assert.Single(window.GetVisualDescendants().OfType<Artwork>(), control => control.SourceUri == uri));
            Assert.Same(source, artwork.Source);
            Assert.Equal(0, listChanges);
            Assert.Equal(0, sourceChanges);
            Assert.Equal(0, languageChanges);
            Assert.False(Directory.Exists(paths.GetPackageConfigurationDirectory(PackageNames.Desktop)));
            await runtime.Settings.SaveAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal([nameof(DesktopLyricsPreferences) + ".json"], Directory.EnumerateFiles(paths.GetPackageConfigurationDirectory(PackageNames.Desktop)).Select(Path.GetFileName));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SettingsApplyImmediatelyAndNotificationsStayWithinTheirModule()
    {
        using var paths = new TemporaryPaths();
        await using var runtime = new DesktopRuntime(paths, new(paths.PluginDirectory, paths.MusicLibraryDirectory));
        var scope = runtime.Settings.ForPackage(PackageNames.Desktop);
        DesktopPreferences.Register(scope);
        using var text = new LocalizationService(scope);
        using var appearance = new AppearanceService(Avalonia.Application.Current!, scope);
        await using var main = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        await main.InitializeAsync();
        await runtime.Settings.StopAsync();
        var languageChanges = 0;
        var appearanceChanges = 0;
        text.Changed += (_, _) => languageChanges++;
        using var subscription = scope.Subscribe<AppearancePreferences>(_ => appearanceChanges++);
        main.Lyrics.DesktopLyricFontSize = 200;
        Assert.Equal(48, main.Lyrics.DesktopLyricFontSize);
        Assert.Equal(48, scope.Get<DesktopLyricsPreferences>().FontSize);
        main.Lyrics.LyricFontSize = 18;
        Assert.Equal(18, scope.Get<LyricsPreferences>().FontSize);
        Assert.Equal(0, languageChanges);
        Assert.Equal(0, appearanceChanges);
        scope.Apply<LanguagePreferences>(current => current with { Language = LanguagePreference.English });
        scope.Apply<LanguagePreferences>(current => current with { Language = LanguagePreference.English });
        Assert.Equal(1, languageChanges);
        Assert.Equal(0, appearanceChanges);
        Assert.False(Directory.Exists(paths.GetPackageConfigurationDirectory(PackageNames.Desktop)));
    }
}
