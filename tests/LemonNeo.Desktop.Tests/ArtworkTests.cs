using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Application;
using LemonNeo.Configuration;
using LemonNeo.Core;
using LemonNeo.Desktop.Controls;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.ViewModels;
using LemonNeo.Desktop.Views;
using LemonNeo.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class ArtworkTests
{
    private static readonly Color CoverColor = Color.FromRgb(0xE2, 0x43, 0x43);

    [AvaloniaFact]
    public async Task LocalCoverLoadsUnicodeFileUriAndReleasesTheFile()
    {
        using var paths = new TestPaths();
        var path = WriteCover(paths);
        using var client = new HttpClient();
        using var bitmap = await CreateService(client).LoadAsync(new Uri(path));
        Assert.NotNull(bitmap);
        Assert.Equal(new PixelSize(512, 256), bitmap.PixelSize);
        // The file must not remain locked while the cover is displayed.
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(file.Length > 0);
    }

    [AvaloniaTheory]
    [InlineData("http")]
    [InlineData("https")]
    public async Task NetworkCoverDecodesAndDisposesTheResponse(string scheme)
    {
        var uri = new Uri($"{scheme}://covers.example/album.png?signature=123");
        var content = new TrackedContent(CreateCover());
        using var handler = new TestHandler((request, _) =>
        {
            Assert.False(Dispatcher.UIThread.CheckAccess());
            Assert.Equal(uri, request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var client = new HttpClient(handler);
        using var bitmap = await CreateService(client).LoadAsync(uri);
        Assert.NotNull(bitmap);
        Assert.Equal(new PixelSize(512, 256), bitmap.PixelSize);
        Assert.True(content.Disposed.Task.IsCompletedSuccessfully);
    }

    [AvaloniaFact]
    public async Task MissingCorruptUnsupportedAndFailedCoversReturnThePlaceholder()
    {
        using var paths = new TestPaths();
        using var handler = new TestHandler((request, _) =>
        {
            var response = new HttpResponseMessage(request.RequestUri!.AbsolutePath == "/missing"
                ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            if (request.RequestUri.AbsolutePath == "/large") response.Content.Headers.ContentLength = 17 * 1024 * 1024;
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var service = CreateService(client);
        foreach (var uri in new[]
        {
            new Uri(Path.Combine(paths.DataDirectory, "absent.png")),
            new Uri("https://covers.example/missing"), new Uri("https://covers.example/corrupt"),
            new Uri("https://covers.example/large"), new Uri("ftp://covers.example/album.png"),
            new Uri("album.png", UriKind.Relative)
        }) Assert.Null(await service.LoadAsync(uri));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadAsync(new Uri("https://covers.example/album.png"), cancellation.Token));
    }

    [AvaloniaFact]
    public async Task AlreadyRestoredSessionRendersTheSameCoverInBothViewsAndPalettes()
    {
        using var paths = new TestPaths();
        var track = CreateTrack(paths, "Restored", new Uri(WriteCover(paths)));
        var item = new PlaybackQueueItem { Track = track };
        await new JsonPlaybackStateRepository(paths.MusicLibraryDirectory).SaveAsync(new()
        {
            Items = [item], CurrentItemId = item.Id, Status = PlaybackStatus.Paused
        });
        await using var runtime = CreateRuntime(paths);
        await runtime.InitializeAsync(CancellationToken.None);
        var text = new LocalizationService();
        var appearance = new AppearanceService(Avalonia.Application.Current!, text, runtime.Get<IAppConfigurationStore>());
        await using var vm = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        await vm.InitializeAsync();
        await WaitUntilAsync(() => vm.Playback.NowArtwork is not null);
        Assert.False(vm.Playback.IsPlaying);
        await vm.Navigation.NavigateAsync(DesktopRoutes.Lyrics);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                await appearance.SetAsync(new(theme, LanguagePreference.English, true));
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var covers = window.GetVisualDescendants().OfType<Artwork>().ToArray();
                Assert.Equal(2, covers.Length);
                using var rendered = new RenderTargetBitmap(new PixelSize(1180, 790));
                rendered.Render(window);
                foreach (var cover in covers)
                {
                    Assert.Same(vm.Playback.NowArtwork, cover.Source);
                    Assert.Same(vm.Playback.NowArtwork, Assert.Single(cover.GetVisualDescendants().OfType<Image>()).Source);
                    Assert.False(Assert.Single(cover.GetVisualDescendants().OfType<Viewbox>()).IsVisible);
                    Assert.True(cover.IsEffectivelyVisible);
                    var center = cover.TranslatePoint(new Point(cover.Bounds.Width / 2, cover.Bounds.Height / 2), window)!.Value;
                    Assert.Equal(CoverColor, ReadPixel(rendered, center));
                }
                if (Environment.GetEnvironmentVariable("LEMONNEO_PREVIEW_DIRECTORY") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    rendered.Save(Path.Combine(directory, $"Artwork-{theme}.png"), PngBitmapEncoderOptions.Default);
                }
            }

            await vm.Playback.ClearQueueCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(vm.Playback.NowArtwork);
            foreach (var cover in window.GetVisualDescendants().OfType<Artwork>())
            {
                Assert.Null(cover.Source);
                Assert.True(Assert.Single(cover.GetVisualDescendants().OfType<Viewbox>()).IsVisible);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SwitchingCoversCancelsOldRequestsAndIgnoresTheirLateResponses()
    {
        using var paths = new TestPaths();
        var slowStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateContent = new TrackedContent(CreateCover());
        var newCover = CreateCover(Colors.Blue);
        var requests = new ConcurrentQueue<string>();
        using var handler = new TestHandler((request, token) =>
        {
            requests.Enqueue(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/old")
            {
                slowStarted.SetResult(token);
                // Simulate a provider completing despite cancellation.
                return slowResponse.Task;
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(newCover)
            });
        });
        using var client = new HttpClient(handler);
        await using var runtime = CreateRuntime(paths);
        var text = new LocalizationService();
        var appearance = new AppearanceService(Avalonia.Application.Current!, text, runtime.Get<IAppConfigurationStore>());
        await using var vm = new MainViewModel(runtime, text, appearance, CreateService(client));
        await vm.InitializeAsync();
        var session = runtime.Get<PlaybackSessionService>();
        try
        {
            var oldTrack = CreateTrack(paths, "Old", new Uri("https://covers.example/old"));
            await SelectTrackAsync(session, vm, oldTrack);
            await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Metadata can change while the provider-qualified track identity stays the same.
            var newTrack = oldTrack with { Title = "New", ArtworkUri = new Uri("https://covers.example/new") };
            await SelectTrackAsync(session, vm, newTrack);
            await WaitUntilAsync(() => vm.Playback.NowArtwork is not null);
            var currentCover = vm.Playback.NowArtwork;
            Assert.True(slowStarted.Task.Result.IsCancellationRequested);

            slowResponse.SetResult(new(HttpStatusCode.OK) { Content = lateContent });
            await lateContent.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(currentCover, vm.Playback.NowArtwork);

            await vm.Playback.TogglePlaybackCommand.ExecuteAsync(null);
            await vm.Playback.SeekAsync(7);
            await SelectTrackAsync(session, vm, newTrack with { Id = new("local", "shared-cover"), Title = "Shared cover" });
            Assert.Same(currentCover, vm.Playback.NowArtwork);
            Assert.Equal(new[] { "/old", "/new" }, requests.ToArray());

            await SelectTrackAsync(session, vm, CreateTrack(paths, "No cover", null));
            Assert.Null(vm.Playback.NowArtwork);
            Assert.False(vm.Playback.HasError);
        }
        finally
        {
            // Always release the deliberately uncooperative request before view-model teardown.
            if (!slowResponse.Task.IsCompleted)
                slowResponse.SetResult(new(HttpStatusCode.OK) { Content = lateContent });
        }
    }

    [AvaloniaFact]
    public async Task ClosingCancelsArtworkLoadingWithoutWaitingForTheNetworkTimeout()
    {
        using var paths = new TestPaths();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new TestHandler(async (_, token) =>
        {
            started.SetResult(token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        await using var runtime = CreateRuntime(paths);
        var text = new LocalizationService();
        var appearance = new AppearanceService(Avalonia.Application.Current!, text, runtime.Get<IAppConfigurationStore>());
        var vm = new MainViewModel(runtime, text, appearance, CreateService(client));
        try
        {
            await vm.InitializeAsync();
            await SelectTrackAsync(runtime.Get<PlaybackSessionService>(), vm,
                CreateTrack(paths, "Pending", new Uri("https://covers.example/pending")));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await vm.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.True(started.Task.Result.IsCancellationRequested);
        Assert.Null(vm.Playback.NowArtwork);
    }

    private static ArtworkService CreateService(HttpClient client) => new(client, NullLogger<ArtworkService>.Instance);

    private static DesktopRuntime CreateRuntime(TestPaths paths) => new(paths,
        new(Path.Combine(AppContext.BaseDirectory, "plugins"), paths.MusicLibraryDirectory), [new DesktopTests.TestPlayerProvider()]);

    private static Track CreateTrack(TestPaths paths, string title, Uri? artwork)
    {
        Directory.CreateDirectory(paths.DataDirectory);
        var path = Path.Combine(paths.DataDirectory, "test.wav");
        File.WriteAllBytes(path, [0]);
        return new Track
        {
            Id = new("local", title), Title = title, ArtworkUri = artwork,
            ExternalIds = [new(MusicIdNamespaces.File, path, "path")]
        };
    }

    private static async Task SelectTrackAsync(PlaybackSessionService session, MainViewModel vm, Track track)
    {
        await session.PlayPlaylistAsync([track]);
        await WaitUntilAsync(() => vm.Playback.NowTitle == track.Title);
    }

    private static string WriteCover(TestPaths paths)
    {
        Directory.CreateDirectory(paths.DataDirectory);
        var path = Path.Combine(paths.DataDirectory, "封面 # 1.png");
        File.WriteAllBytes(path, CreateCover());
        return path;
    }

    private static byte[] CreateCover(Color? color = null)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(700, 350));
        using (var context = bitmap.CreateDrawingContext())
            context.DrawRectangle(new SolidColorBrush(color ?? CoverColor), null, new Rect(0, 0, 700, 350));
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    private static Color ReadPixel(Bitmap bitmap, Point point)
    {
        using var copy = new WriteableBitmap(bitmap.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = copy.Lock();
        bitmap.CopyPixels(buffer);
        var offset = (int)point.Y * buffer.RowBytes + (int)point.X * 4;
        return Color.FromArgb(Marshal.ReadByte(buffer.Address, offset + 3), Marshal.ReadByte(buffer.Address, offset + 2),
            Marshal.ReadByte(buffer.Address, offset + 1), Marshal.ReadByte(buffer.Address, offset));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "The artwork or playback state did not update in time.");
            await Task.Delay(10);
        }
    }

    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class TrackedContent(byte[] bytes) : ByteArrayContent(bytes)
    {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) Disposed.TrySetResult();
        }
    }
}
