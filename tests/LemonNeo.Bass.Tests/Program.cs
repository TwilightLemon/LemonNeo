using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Playback;
using LemonNeo.Plugin.Abstractions;
using LemonNeo.Plugin.Bass;
using LemonNeo.Plugin.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Un4seen.Bass;
using BassApi = Un4seen.Bass.Bass;

// Use real BASS and silent audio, including an HTTP response stalled before
// the saved position. No user library, network service, or credentials are used.
NativeLibrary.SetDllImportResolver(typeof(BassApi).Assembly, (name, _, _) =>
{
    var file = OperatingSystem.IsWindows() ? name + ".dll" : OperatingSystem.IsMacOS() ? "lib" + name + ".dylib" : "lib" + name + ".so";
    var native = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", file);
    return File.Exists(native) ? NativeLibrary.Load(native) : nint.Zero;
});
var plugin = new BassPlayerPlugin();
var testCache = Path.Combine(Path.GetTempPath(), "LemonNeo-bass-cache-" + Guid.NewGuid().ToString("N"));
await plugin.InitializeAsync(new TestContext(testCache), CancellationToken.None);
try
{
    var wave = SilentWave();
    await using var server = new SlowAudioServer(wave);
    var player = (ISeekablePlayer)plugin.Player;
    var track = new Track { Id = new MusicItemId("test", "silent"), Title = "Silent seek test", Duration = TimeSpan.FromSeconds(60) };
    var source = new HttpPlaybackSource { Uri = server.Uri, CacheKey = "test:silent:wav:standard" };
    await player.PlayAsync(track, source);

    var stream = (int)player.GetType().GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;
    var bytes = BassApi.BASS_ChannelSeconds2Bytes(stream, 45);
    var immediateSeek = BassApi.BASS_ChannelSetPosition(stream, bytes);
    var immediateError = BassApi.BASS_ErrorGetCode();
    Check(!immediateSeek, "The server did not reproduce seeking past the downloaded data.");
    Console.WriteLine($"Original immediate seek: {immediateError}");

    var seek = player.SeekAsync(TimeSpan.FromSeconds(45));
    await Task.Delay(300);
    Check(!seek.IsCompleted && player.State == PlayerState.Paused, "Seek did not wait quietly for the download.");
    server.Release();
    await seek.WaitAsync(TimeSpan.FromSeconds(10));
    Check(player.State == PlayerState.Playing && player.Timeline.Position.TotalSeconds is >= 45 and < 47,
        "The downloaded stream did not resume at the requested position.");
    var musicCache = Path.Combine(testCache, "Music");
    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
    {
        while (Directory.GetFiles(musicCache, "*.wav").Length == 0)
            await Task.Delay(50, timeout.Token);
    }
    var cachedFile = Directory.GetFiles(musicCache, "*.wav").Single();
    Check((await File.ReadAllBytesAsync(cachedFile)).SequenceEqual(wave), "The cache does not contain the complete original audio bytes.");
    Check(Directory.GetFiles(musicCache, "*.part").Length == 0, "Completed caching left a temporary file.");
    await player.StopAsync();
    var requests = server.RequestCount;
    await plugin.ShutdownAsync(CancellationToken.None);
    await plugin.InitializeAsync(new TestContext(testCache), CancellationToken.None);
    player = (ISeekablePlayer)plugin.Player;
    await player.PlayAsync(track, source with { Uri = new Uri("http://127.0.0.1:1/expired-token.wav"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
    await player.SeekAsync(TimeSpan.FromSeconds(45));
    Check(server.RequestCount == requests && player.Timeline.Position.TotalSeconds >= 45,
        "A completed cache was not reused after restarting the player and rotating the URL.");
    await player.StopAsync();

    // The application must retain the restored position after cancellation,
    // so a second resume needs no stop and does not restart from zero.
    await using var cancelledServer = new SlowAudioServer(wave);
    var registry = new TestRegistry(new HttpPlaybackSource { Uri = cancelledServer.Uri });
    var discovery = new PlaybackSourceDiscoveryService(registry, new MetadataService(registry));
    var resolver = new PlaybackResolver(registry, registry, new PlayerRegistry([plugin]), discovery);
    var queue = new PlaybackQueue();
    queue.Replace([track]);
    var repository = new TestRepository(queue.Snapshot() with { Position = TimeSpan.FromSeconds(45), Status = PlaybackStatus.Paused });
    await using (var session = new PlaybackSessionService(resolver, new PlaybackQueue(), repository, NullLogger<PlaybackSessionService>.Instance))
    {
        await session.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        var resume = session.ResumeAsync(cancellation.Token);
        await Task.Delay(300);
        cancellation.Cancel();
        try { await resume; throw new InvalidOperationException("Seek ignored cancellation."); }
        catch (OperationCanceledException) { }
        await session.SaveAsync();
        Check(repository.State.Position.TotalSeconds == 45 && session.Snapshot.State == PlayerState.Stopped,
            "Failed resume lost the saved position or left audio playing.");
        Check(Directory.GetFiles(musicCache, "*.part").Length == 0 && Directory.GetFiles(musicCache, "*.wav").Length == 1,
            "An interrupted download was published as a complete cache or left a temporary file.");
        cancelledServer.Release();
        await session.ResumeAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(session.Snapshot.State == PlayerState.Playing && session.Snapshot.Timeline.Position.TotalSeconds >= 45,
            "Retrying resume did not restore the retained position.");
    }

    var temporaryWave = Path.Combine(Path.GetTempPath(), "LemonNeo-seek-" + Guid.NewGuid().ToString("N") + ".wav");
    await File.WriteAllBytesAsync(temporaryWave, wave);
    try
    {
        await player.PlayAsync(track, new FilePlaybackSource(temporaryWave));
        await player.SeekAsync(TimeSpan.FromSeconds(30));
        Check(player.State == PlayerState.Playing && player.Timeline.Position.TotalSeconds is >= 30 and < 32, "Local seeking regressed.");
        await player.PauseAsync();
        await player.SeekAsync(TimeSpan.FromSeconds(20));
        Check(player.State == PlayerState.Paused && player.Timeline.Position.TotalSeconds is >= 20 and < 21, "Seeking a paused stream resumed it.");
        await player.StopAsync();
    }
    finally { File.Delete(temporaryWave); }

    // A malformed cached file must be replaced by a fresh network download.
    await File.WriteAllBytesAsync(cachedFile, "not audio"u8.ToArray());
    await using (var replacementServer = new SlowAudioServer(wave))
    {
        replacementServer.Release();
        await player.PlayAsync(track, source with { Uri = replacementServer.Uri });
        Check(replacementServer.RequestCount == 1, "A corrupt cache did not fall back to downloading audio.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(cachedFile)) await Task.Delay(50, timeout.Token);
        Check((await File.ReadAllBytesAsync(cachedFile)).SequenceEqual(wave), "A corrupt cache was not replaced by complete audio.");
        await player.StopAsync();
    }

    // A configured cache directory that cannot be created must not stop playback.
    await plugin.ShutdownAsync(CancellationToken.None);
    var blockedCache = Path.Combine(testCache, "cache-is-a-file");
    await File.WriteAllTextAsync(blockedCache, "fixture");
    await plugin.InitializeAsync(new TestContext(blockedCache), CancellationToken.None);
    player = (ISeekablePlayer)plugin.Player;
    await using (var uncachedServer = new SlowAudioServer(wave))
    {
        uncachedServer.Release();
        await player.PlayAsync(track, new HttpPlaybackSource { Uri = uncachedServer.Uri });
        Check(player.State == PlayerState.Playing, "A cache write failure stopped network playback.");
        await player.StopAsync();
    }
    Console.WriteLine("BASS disk caching, cache reuse after restart/URL rotation, partial cleanup, buffered resume, cancellation/retry, and local seek checks passed.");
}
finally
{
    await plugin.ShutdownAsync(CancellationToken.None);
    if (Directory.Exists(testCache)) Directory.Delete(testCache, recursive: true);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static byte[] SilentWave()
{
    const int rate = 11025;
    const int dataLength = rate * 2 * 60;
    using var memory = new MemoryStream();
    using var writer = new BinaryWriter(memory);
    writer.Write("RIFF"u8); writer.Write(dataLength + 36); writer.Write("WAVEfmt "u8);
    writer.Write(16); writer.Write((short)1); writer.Write((short)1);
    writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
    writer.Write("data"u8); writer.Write(dataLength); writer.Write(new byte[dataLength]);
    return memory.ToArray();
}

sealed class SlowAudioServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _serving;
    private int _requestCount;
    public Uri Uri { get; }
    public int RequestCount => Volatile.Read(ref _requestCount);

    public SlowAudioServer(byte[] data)
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        Uri = new Uri($"http://127.0.0.1:{port}/silent.wav");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _serving = Task.Run(async () =>
        {
            while (!_cancellation.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().WaitAsync(_cancellation.Token); }
                catch (Exception) when (_cancellation.IsCancellationRequested) { return; }
                Interlocked.Increment(ref _requestCount);
                try
                {
                    context.Response.ContentType = "audio/wav";
                    context.Response.ContentLength64 = data.Length;
                    await context.Response.OutputStream.WriteAsync(data.AsMemory(0, 262144), _cancellation.Token);
                    await context.Response.OutputStream.FlushAsync(_cancellation.Token);
                    await _release.Task.WaitAsync(_cancellation.Token);
                    await context.Response.OutputStream.WriteAsync(data.AsMemory(262144), _cancellation.Token);
                }
                catch (Exception exception) when (exception is IOException or HttpListenerException or OperationCanceledException ||
                    exception is ObjectDisposedException && _cancellation.IsCancellationRequested) { }
                finally
                {
                    try { context.Response.Close(); }
                    catch (ObjectDisposedException) when (_cancellation.IsCancellationRequested) { }
                }
            }
        });
    }

    public void Release() => _release.TrySetResult();
    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _listener.Close();
        await _serving;
        _cancellation.Dispose();
    }
}

sealed class TestContext(string cacheDirectory) : IPluginContext
{
    public ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;
    public IHttpClientFactory HttpClientFactory => throw new NotSupportedException();
    public IPluginStorage Storage => throw new NotSupportedException();
    public ICredentialVault Credentials => throw new NotSupportedException();
    public string CacheDirectory => cacheDirectory;
    public void RegisterProvider(IMusicProvider provider) => throw new NotSupportedException();
    public void RegisterPlayer(IPlayerProvider player) { }
}

sealed class TestRegistry(PlaybackSource source) : IProviderRegistry, IPlaybackSourceProviderRegistry, IMusicProvider, IPlaybackProvider
{
    public string Id => "test";
    public ProviderDescriptor Descriptor { get; } = new("Test", "Local HTTP fixture");
    public IReadOnlyCollection<IMusicProvider> Providers => [this];
    public IReadOnlyCollection<PlaybackSourceProviderRegistration> Sources => [new(Id, this, this, [])];
    public IMusicProvider? Get(string id) => id == Id ? this : null;
    PlaybackSourceProviderRegistration? IPlaybackSourceProviderRegistry.Get(string id) => id == Id ? Sources.Single() : null;
    public Task<PlaybackSource?> ResolvePlaybackAsync(Track track, PlaybackRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult<PlaybackSource?>(source);
}

sealed class TestRepository(PlaybackQueueState state) : IPlaybackStateRepository
{
    public PlaybackQueueState State { get; private set; } = state;
    public Task<PlaybackQueueState> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
    public Task SaveAsync(PlaybackQueueState value, CancellationToken cancellationToken = default)
    {
        State = value;
        return Task.CompletedTask;
    }
}
