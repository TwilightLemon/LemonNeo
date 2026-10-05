using System.Net;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.ViewModels;
using LemonNeo.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class PlaybackArtworkTests
{
    [AvaloniaFact]
    public async Task DelayedOldCoverCannotReplaceTheNewTrackAndMissingArtworkFallsBack()
    {
        using var paths = new TestPaths();
        await using var runtime = new DesktopRuntime(paths, new DesktopOptions(Path.Combine(paths.DataDirectory, "plugins"), paths.MusicLibraryDirectory));
        using var appearance = new AppearanceService(Avalonia.Application.Current!, new LocalizationService(), new AppearanceTests.MemoryStore());
        await appearance.SetAsync(new UiPreferences(Theme: ThemePreference.Dark, ReduceMotion: true,
            ThemeColor: ThemeColorPreset.MoonGold, UseArtworkColors: true));
        var context = new DesktopContext(runtime, new LocalizationService(), appearance);
        var session = runtime.Get<PlaybackSessionService>();
        await session.InitializeAsync();
        using var handler = new DelayedArtworkHandler(CreatePng(Colors.Red), CreatePng(Colors.Teal));
        using var client = new HttpClient(handler);
        await using var cache = new ImageCache(paths.CacheDirectory, client, NullLogger<ImageCache>.Instance);
        var artwork = new ArtworkService(cache, NullLogger<ArtworkService>.Instance);
        await using var playback = new PlaybackViewModel(context, artwork);
        var queue = runtime.Get<IPlaybackQueue>();
        async Task SelectAsync(string id, Uri? uri)
        {
            queue.Replace([new Track { Id = new("test", id), Title = id, ArtworkUri = uri }]);
            await session.SetModeAsync(PlaybackMode.Sequential);
            playback.UpdateSnapshot();
        }
        try
        {
            await SelectAsync("old", new Uri("https://artwork.test/old"));
            await handler.OldStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await SelectAsync("new", new Uri("https://artwork.test/new"));
            await WaitUntilAsync(() => playback.NowArtwork is not null && appearance.Colors.IsUsingArtwork);
            var resources = Avalonia.Application.Current!.Resources.MergedDictionaries.OfType<ThemeResources>().Single();
            var currentColor = resources.Snapshot()["Accent"];
            handler.ReleaseOld.TrySetResult();
            await handler.OldCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(80);
            Assert.Equal(currentColor, resources.Snapshot()["Accent"]);
            Assert.Equal("new", playback.NowTitle);
            await SelectAsync("missing", null);
            Assert.Null(playback.NowArtwork);
            Assert.False(appearance.Colors.IsUsingArtwork);
            var fallback = ThemePalette.Create(ThemePalette.Get(ThemeColorPreset.MoonGold), true)["Accent"];
            Assert.Equal(fallback, resources.Snapshot()["Accent"]);
            await SelectAsync("invalid", new Uri("https://artwork.test/invalid"));
            await handler.InvalidCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(80);
            Assert.Null(playback.NowArtwork);
            Assert.False(appearance.Colors.IsUsingArtwork);
            Assert.Equal(fallback, resources.Snapshot()["Accent"]);
        }
        finally { handler.ReleaseOld.TrySetResult(); }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static byte[] CreatePng(Color color)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(20, 20), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var framebuffer = bitmap.Lock())
        {
            var data = new byte[framebuffer.RowBytes * 20];
            for (var y = 0; y < 20; y++)
            for (var x = 0; x < 20; x++)
            {
                var offset = y * framebuffer.RowBytes + x * 4;
                data[offset] = color.B; data[offset + 1] = color.G; data[offset + 2] = color.R; data[offset + 3] = 255;
            }
            Marshal.Copy(data, 0, framebuffer.Address, data.Length);
        }
        using var output = new MemoryStream();
        bitmap.Save(output, PngBitmapEncoderOptions.Default);
        return output.ToArray();
    }

    private sealed class DelayedArtworkHandler(byte[] oldArtwork, byte[] newArtwork) : HttpMessageHandler
    {
        public TaskCompletionSource OldStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OldCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InvalidCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/old")
            {
                OldStarted.TrySetResult();
                // Simulate a server that returns late even after the client cancels the old request.
                await ReleaseOld.Task;
                OldCompleted.TrySetResult();
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(oldArtwork) };
            }
            if (request.RequestUri.AbsolutePath == "/invalid")
            {
                InvalidCompleted.TrySetResult();
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(newArtwork) };
        }
    }
}
