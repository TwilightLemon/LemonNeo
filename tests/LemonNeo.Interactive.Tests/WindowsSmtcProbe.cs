#if WINDOWS
using System.Runtime.InteropServices;
using LemonNeo.Plugin.Abstractions;
using LemonNeo.Plugin.WindowsSmtc;
using Windows.Media.Control;

internal static class WindowsSmtcProbe
{
    public static async Task Run()
    {
        await using var fixture = await Fixture.Create();
        var plugin = new WindowsSmtcPlugin();
        var coverPath = Path.Combine(Path.GetTempPath(), "lemonneo-smtc-cover-" + Guid.NewGuid().ToString("N") + ".png");
        var probeTrack = Fixture.First with { Title = "LemonNeo SMTC probe " + Guid.NewGuid().ToString("N"), ArtworkUri = new Uri(coverPath) };
        await File.WriteAllBytesAsync(coverPath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aZ/QAAAAASUVORK5CYII="));
        try
        {
            await plugin.InitializeAsync(new TestContext(fixture.Interaction), CancellationToken.None);
            await fixture.Session.PlayPlaylistAsync([probeTrack, Fixture.Second]);
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            GlobalSystemMediaTransportControlsSession? native = null;
            await Until(async () =>
            {
                foreach (var session in manager.GetSessions())
                {
                    GlobalSystemMediaTransportControlsSessionMediaProperties metadata;
                    try { metadata = await session.TryGetMediaPropertiesAsync(); }
                    // Other applications' sessions can become unavailable while
                    // enumerating them. Retry discovery for ERROR_NOT_READY.
                    catch (COMException exception) when (exception.HResult == unchecked((int)0x80070015)) { continue; }
                    if (metadata.Title != probeTrack.Title) continue;
                    if (metadata.Artist != "Test Artist" || metadata.AlbumTitle != "Test Album")
                        throw new InvalidOperationException("Native metadata mismatch.");
                    if (metadata.Thumbnail is null) return false;
                    using var image = await metadata.Thumbnail.OpenReadAsync();
                    if (image.Size != (ulong)new FileInfo(coverPath).Length) throw new InvalidOperationException("Native artwork mismatch.");
                    native = session;
                    return true;
                }
                return false;
            });
            if (!await native!.TryPauseAsync()) throw new InvalidOperationException("Native pause was rejected.");
            await Until(() => Task.FromResult(fixture.Interaction.Snapshot.State == PlayerState.Paused));
            if (!await native.TryPlayAsync()) throw new InvalidOperationException("Native play was rejected.");
            await Until(() => Task.FromResult(fixture.Interaction.Snapshot.State == PlayerState.Playing));
            if (!await native.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(7).Ticks))
                throw new InvalidOperationException("Native seek was rejected.");
            await Until(() => Task.FromResult(fixture.Interaction.Snapshot.Position.TotalSeconds == 7));
            await Until(() => Task.FromResult(native.GetTimelineProperties().Position.TotalSeconds == 7));
            if (!await native.TrySkipNextAsync()) throw new InvalidOperationException("Native next was rejected.");
            await Until(async () => (await native.TryGetMediaPropertiesAsync()).Title == Fixture.Second.Title);
            if ((await native.TryGetMediaPropertiesAsync()).Thumbnail is not null)
                throw new InvalidOperationException("Previous artwork must be cleared when the next track has no cover.");
            if (!await native.TrySkipPreviousAsync()) throw new InvalidOperationException("Native previous was rejected.");
            await Until(() => Task.FromResult(fixture.Interaction.Snapshot.Track?.Id == Fixture.First.Id));
            if (!await native.TryStopAsync()) throw new InvalidOperationException("Native stop was rejected.");
            await Until(() => Task.FromResult(fixture.Interaction.Snapshot.State == PlayerState.Stopped));
            await fixture.Session.ClearQueueAsync();
            await plugin.ShutdownAsync(CancellationToken.None);
            await Until(() => Task.FromResult(native.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed));
        }
        finally { await plugin.ShutdownAsync(CancellationToken.None); File.Delete(coverPath); }
    }
    private static async Task Until(Func<Task<bool>> check)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (!await check())
        {
            if (DateTime.UtcNow >= end) throw new TimeoutException("Windows SMTC did not expose the expected session or state.");
            await Task.Delay(100);
        }
    }
}
#endif
