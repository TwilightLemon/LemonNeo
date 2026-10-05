using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Desktop.Controls;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.ViewModels;
using LemonNeo.Desktop.Views;
using LemonNeo.Plugin.Abstractions;
using LemonNeo.Playback;
using SkiaSharp;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class TrackArtworkTests
{
    [AvaloniaTheory]
    [InlineData("playlist")]
    [InlineData("library")]
    [InlineData("search")]
    [InlineData("queue")]
    public async Task TrackListsShowRoundedCoversAndPlaceholders(string scope)
    {
        using var paths = new TestPaths();
        Directory.CreateDirectory(paths.DataDirectory);
        var imagePath = Path.Combine(paths.DataDirectory, "cover.png");
        await File.WriteAllBytesAsync(imagePath, ImageCacheTests.CreatePng(SKColors.Teal));
        var tracks = new[]
        {
            new Track { Id = new("cover-fixture", "a"), Title = "A covered track", ArtworkUri = new Uri(imagePath) },
            new Track { Id = new("cover-fixture", "b"), Title = "B missing artwork" }
        };
        await using var runtime = new DesktopRuntime(paths,
            new DesktopOptions(Path.Combine(paths.DataDirectory, "plugins"), paths.MusicLibraryDirectory),
            hostProviders: [new SearchProvider(tracks)]);
        var text = new LocalizationService();
        using var appearance = new AppearanceService(Avalonia.Application.Current!, text, new AppearanceTests.MemoryStore());
        await appearance.SetAsync(new UiPreferences(Language: LanguagePreference.English, ReduceMotion: true));
        await using var shell = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        await shell.InitializeAsync();
        Assert.False(shell.HasError, shell.Error);
        var playlists = runtime.Get<PlaylistService>();
        var playlist = playlists.Create("Artwork playlist");
        foreach (var track in tracks) playlists.Add(playlist.Id, track);
        shell.SharedContext.RefreshLibrary();
        await using var queue = new PlaybackQueueViewModel(shell.SharedContext, shell.Playback);
        Control view;
        if (scope == "queue")
        {
            runtime.Get<IPlaybackQueue>().Replace(tracks);
            shell.Playback.UpdateSnapshot();
            view = new QueueDrawerView { DataContext = queue };
        }
        else if (scope == "search")
        {
            var page = shell.Navigation.GetPage<SearchViewModel>(DesktopRoutes.Search);
            page.SearchQuery = "artwork";
            await page.SearchAsync();
            Assert.False(page.HasError, page.Error);
            view = new SearchView { DataContext = page };
        }
        else
        {
            var page = shell.Navigation.GetPage<LibraryViewModel>(DesktopRoutes.Library, scope == "playlist" ? playlist.Id : null);
            view = new LibraryView { DataContext = page };
        }
        var window = new Window { Width = 900, Height = 550, Content = view };
        window.Show();
        try
        {
            var covers = view.GetVisualDescendants().OfType<Artwork>().ToArray();
            Assert.Equal(2, covers.Length);
            var covered = covers.Single(cover => cover.SourceUri is not null);
            var missing = covers.Single(cover => cover.SourceUri is null);
            await WaitUntilAsync(() => covered.Source is not null);
            Assert.Equal(new CornerRadius(8), covered.CornerRadius);
            Assert.Equal(Colors.Teal, ArtworkColorExtractor.Extract(Assert.IsType<Bitmap>(covered.Source)));
            Assert.Null(missing.Source);
            Assert.True(Placeholder(missing).IsVisible);
            Assert.False(Placeholder(covered).IsVisible);
            Assert.Single(Directory.GetFiles(runtime.Get<ImageCache>().DirectoryPath, "*.image"));

            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            using var stream = new MemoryStream();
            frame.Save(stream, PngBitmapEncoderOptions.Default);
            stream.Position = 0;
            using var pixels = SKBitmap.Decode(stream);
            var position = covered.TranslatePoint(new Point(), window)!.Value;
            var center = pixels.GetPixel((int)(position.X + covered.Bounds.Width / 2), (int)(position.Y + covered.Bounds.Height / 2));
            var corner = pixels.GetPixel((int)position.X, (int)position.Y);
            Assert.Equal(SKColors.Teal, center);
            Assert.NotEqual(center, corner);

            var previewDirectory = Environment.GetEnvironmentVariable("LEMONNEO_ARTWORK_PREVIEW_DIR");
            if (!string.IsNullOrWhiteSpace(previewDirectory))
            {
                Directory.CreateDirectory(previewDirectory);
                frame.Save(Path.Combine(previewDirectory, scope + ".png"), PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RecycledArtworkCannotDisplayAnOldRequestAndDetachingReleasesItsImage()
    {
        using var paths = new TestPaths();
        using var handler = new RecycledArtworkHandler();
        using var client = new HttpClient(handler);
        await using var cache = ImageCacheTests.Cache(paths, client);
        var artwork = new Artwork { Width = 44, Height = 44, Loader = ImageCacheTests.Artwork(cache), SourceUri = new("https://artwork.test/old") };
        var window = new Window { Width = 100, Height = 100, Content = artwork };
        window.Show();
        try
        {
            await handler.OldStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            artwork.SourceUri = new Uri("https://artwork.test/new");
            await WaitUntilAsync(() => artwork.Source is not null);
            var current = artwork.Source;
            Assert.Equal(Colors.Teal, ArtworkColorExtractor.Extract(Assert.IsType<Bitmap>(current)));
            handler.ReleaseOld.TrySetResult();
            await cache.GetAsync(new Uri("https://artwork.test/old"));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(current, artwork.Source);

            artwork.SourceUri = null;
            Assert.Null(artwork.Source);
            Assert.True(Placeholder(artwork).IsVisible);
            artwork.SourceUri = new Uri("https://artwork.test/new");
            await WaitUntilAsync(() => artwork.Source is not null);
            window.Content = null;
            Assert.Null(artwork.Source);
            window.Content = artwork;
            await WaitUntilAsync(() => artwork.Source is not null);
            Assert.NotSame(current, artwork.Source);
            Assert.Equal(2, handler.Requests);

            artwork.SourceUri = new Uri("https://artwork.test/invalid");
            await handler.InvalidStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitUntilAsync(() => Directory.GetFiles(cache.DirectoryPath, "*.image").Length == 2);
            Assert.Null(artwork.Source);
            Assert.True(Placeholder(artwork).IsVisible);
        }
        finally { handler.ReleaseOld.TrySetResult(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task SharedCoversSurviveWhenAnotherControlIsDetached()
    {
        using var paths = new TestPaths();
        using var handler = new ImageCacheTests.ImageHandler(ImageCacheTests.CreatePng(SKColors.Teal));
        using var client = new HttpClient(handler);
        await using var cache = ImageCacheTests.Cache(paths, client);
        var loader = ImageCacheTests.Artwork(cache);
        var uri = new Uri("https://artwork.test/shared");
        var first = new Artwork { Width = 44, Height = 44, SourceUri = uri, Loader = loader };
        var second = new Artwork { Width = 44, Height = 44, SourceUri = uri, Loader = loader };
        var panel = new StackPanel { Children = { first, second } };
        var window = new Window { Width = 100, Height = 150, Content = panel };
        window.Show();
        try
        {
            await WaitUntilAsync(() => first.Source is not null && second.Source is not null);
            Assert.NotSame(first.Source, second.Source);
            panel.Children.Remove(first);
            Assert.Null(first.Source);
            using var stream = new MemoryStream();
            Assert.IsType<Bitmap>(second.Source).Save(stream, PngBitmapEncoderOptions.Default);
            Assert.True(stream.Length > 0);
            Assert.Equal(1, handler.Requests);
        }
        finally { window.Close(); }
    }

    private static Viewbox Placeholder(Artwork artwork) => artwork.GetVisualDescendants().OfType<Viewbox>().Single();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, timeout.Token);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class SearchProvider(IReadOnlyList<Track> tracks) : IMusicProvider, ISearchProvider, IPlaybackProvider
    {
        public string Id => "cover-fixture";
        public ProviderDescriptor Descriptor { get; } = new("Cover fixture", "Track artwork UI fixture");
        public Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SearchResult(Id, tracks));
        public Task<PlaybackSource?> ResolvePlaybackAsync(Track track, PlaybackRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<PlaybackSource?>(null);
    }

    private sealed class RecycledArtworkHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public TaskCompletionSource OldStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InvalidStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.RequestUri!.AbsolutePath == "/old")
            {
                OldStarted.TrySetResult();
                await ReleaseOld.Task.WaitAsync(cancellationToken);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageCacheTests.CreatePng(SKColors.Red)) };
            }
            if (request.RequestUri.AbsolutePath == "/invalid")
            {
                InvalidStarted.TrySetResult();
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageCacheTests.CreatePng(SKColors.Teal)) };
        }
    }
}
