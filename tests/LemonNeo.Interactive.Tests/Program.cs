using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Playback;
using LemonNeo.Plugin.Abstractions;
using LemonNeo.Plugin.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

var checks = new (string Name, Func<Task> Run)[]
{
    ("Metadata, commands, seeking, persistence, and queue boundaries", Commands),
    ("Restored selection stays stopped and resumes at its saved position", Restore),
    ("Non-seekable players do not advertise or accept seeking", NonSeekable),
    ("Natural completion publishes the next queue track", Completion),
    ("Failed subscribers and plugin workers are isolated and cleaned up", PluginLifetime),
    ("Unsupported platforms are skipped before loading their assemblies", Platforms),
#if WINDOWS
    ("Windows SMTC publishes metadata/artwork/timeline and handles native controls", WindowsSmtcProbe.Run),
#endif
};
foreach (var check in checks)
{
    await check.Run();
    Console.WriteLine($"PASS: {check.Name}");
}
Console.WriteLine($"Passed {checks.Length} Interactive regression checks.");

static async Task Commands()
{
    await using var fixture = await Fixture.Create();
    var updates = new List<InteractivePlaybackSnapshot>();
    fixture.Interaction.Changed += (_, args) => updates.Add(args.Snapshot);
    await fixture.Session.PlayPlaylistAsync([Fixture.First, Fixture.Second]);
    var snapshot = fixture.Interaction.Snapshot;
    Check(snapshot.Track == Fixture.First && snapshot.Track.ArtworkUri == Fixture.First.ArtworkUri, "Original queue metadata and artwork must survive source resolution.");
    Check(fixture.Player.CurrentTrack!.Id != snapshot.Track!.Id, "Fixture must use a distinct source identity.");
    Check(snapshot.Controls.HasFlag(PlaybackControls.Next) && !snapshot.Controls.HasFlag(PlaybackControls.Previous), "Sequential first-track boundaries.");
    await fixture.Interaction.ExecuteAsync(PlaybackCommand.Play);
    Check(fixture.Player.PlayCount == 1, "Play while already playing must not restart audio.");
    await fixture.Interaction.ExecuteAsync(PlaybackCommand.Pause);
    Check(fixture.Interaction.Snapshot.State == PlayerState.Paused, "Pause command.");
    await fixture.Interaction.ExecuteAsync(PlaybackCommand.Seek, TimeSpan.FromSeconds(8));
    Check(fixture.Interaction.Snapshot.Position == TimeSpan.FromSeconds(8) && fixture.Repository.Saved.Position == TimeSpan.FromSeconds(8), "Seek must publish and persist position.");
    await fixture.Interaction.ExecuteAsync(PlaybackCommand.Seek, TimeSpan.FromHours(1));
    Check(fixture.Interaction.Snapshot.Position == Fixture.First.Duration, "Seek clamps to duration.");
    await Throws<ArgumentOutOfRangeException>(() => fixture.Interaction.ExecuteAsync(PlaybackCommand.Seek, TimeSpan.FromSeconds(-1)));
    await Throws<ArgumentException>(() => fixture.Interaction.ExecuteAsync(PlaybackCommand.Seek));
    await fixture.Interaction.ExecuteAsync(PlaybackCommand.TogglePlayPause);
    Check(fixture.Interaction.Snapshot.State == PlayerState.Playing, "Toggle resumes paused audio.");
    await fixture.Interaction.ExecuteAsync(PlaybackCommand.Next);
    Check(fixture.Interaction.Snapshot.Track == Fixture.Second && !fixture.Interaction.Snapshot.Controls.HasFlag(PlaybackControls.Next), "Next track and last-track boundary.");
    await fixture.Interaction.ExecuteAsync(PlaybackCommand.Previous);
    Check(fixture.Interaction.Snapshot.Track == Fixture.First, "Previous track.");
    await fixture.Session.SetModeAsync(PlaybackMode.Shuffle);
    Check(!fixture.Interaction.Snapshot.Controls.HasFlag(PlaybackControls.Previous), "Shuffle cannot go back before the first item in its history.");
    await fixture.Interaction.ExecuteAsync(PlaybackCommand.Next);
    Check(fixture.Interaction.Snapshot.Controls.HasFlag(PlaybackControls.Previous), "Shuffle previous is available after advancing.");
    await fixture.Interaction.ExecuteAsync(PlaybackCommand.Previous);
    Check(fixture.Interaction.Snapshot.Track == Fixture.First, "Shuffle previous follows shuffle history.");
    await fixture.Session.SetModeAsync(PlaybackMode.RepeatAll);
    Check(fixture.Interaction.Snapshot.Controls.HasFlag(PlaybackControls.Previous) && fixture.Interaction.Snapshot.Controls.HasFlag(PlaybackControls.Next), "Repeat-all allows wraparound controls.");
    await fixture.Interaction.ExecuteAsync(PlaybackCommand.Stop);
    Check(fixture.Interaction.Snapshot.State == PlayerState.Stopped && fixture.Interaction.Snapshot.Position == TimeSpan.Zero, "Stop resets position.");
    await fixture.Interaction.ExecuteAsync(PlaybackCommand.Play);
    Check(fixture.Interaction.Snapshot.State == PlayerState.Playing, "Play after stop restarts the selected queue item.");
    await fixture.Session.ClearQueueAsync();
    Check(fixture.Interaction.Snapshot.Track is null && fixture.Interaction.Snapshot.Controls == PlaybackControls.None, "Queue clear removes metadata and all controls.");
    Check(updates.Any(update => update.Track == Fixture.Second), "Track transitions must notify plugins.");
}

static async Task Restore()
{
    var item = new PlaybackQueueItem { Track = Fixture.First };
    var saved = new PlaybackQueueState { Items = [item], CurrentItemId = item.Id, Position = TimeSpan.FromSeconds(12), Status = PlaybackStatus.Playing };
    await using var fixture = await Fixture.Create(saved);
    Check(fixture.Interaction.Snapshot is { State: PlayerState.Stopped, Position.TotalSeconds: 12 } && fixture.Player.PlayCount == 0,
        "Saved playing status must never start audio or advertise playing on launch.");
    Check(!fixture.Interaction.Snapshot.Controls.HasFlag(PlaybackControls.Seek), "Restored idle selection has no active seekable decoder.");
    await fixture.Interaction.ExecuteAsync(PlaybackCommand.Play);
    Check(fixture.Interaction.Snapshot is { State: PlayerState.Playing, Position.TotalSeconds: 12 }, "Play resumes the persisted position.");
}

static async Task NonSeekable()
{
    await using var fixture = await Fixture.Create(seekable: false);
    await fixture.Session.PlayTrackAsync(Fixture.First);
    Check(!fixture.Interaction.Snapshot.Controls.HasFlag(PlaybackControls.Seek), "Seek availability must reflect player capabilities.");
    await Throws<NotSupportedException>(() => fixture.Interaction.ExecuteAsync(PlaybackCommand.Seek, TimeSpan.FromSeconds(1)));
}

static async Task Completion()
{
    await using var fixture = await Fixture.Create();
    await fixture.Session.PlayPlaylistAsync([Fixture.First, Fixture.Second]);
    fixture.Player.HasEnded = true;
    await Until(() => fixture.Interaction.Snapshot.Track == Fixture.Second);
    fixture.Player.HasEnded = true;
    await Until(() => fixture.Interaction.Snapshot.State == PlayerState.Stopped);
}

static async Task PluginLifetime()
{
    await using var fixture = await Fixture.Create();
    fixture.Interaction.Changed += (_, _) => throw new InvalidOperationException("Failed subscriber");
    var plugin = new ProbePlugin();
    await plugin.InitializeAsync(new TestContext(fixture.Interaction), CancellationToken.None);
    await fixture.Session.PlayTrackAsync(Fixture.First);
    await Until(() => plugin.Last?.Track == Fixture.First);
    plugin.FailNextUpdate = true;
    await fixture.Interaction.ExecuteAsync(PlaybackCommand.Pause);
    await Until(() => !plugin.FailNextUpdate);
    Check(plugin.Request(PlaybackCommand.Play), "Native callback must enqueue a command.");
    await Until(() => fixture.Interaction.Snapshot.State == PlayerState.Playing);
    Check(plugin.Request((PlaybackCommand)100), "Queue a failing native command.");
    Check(plugin.Request(PlaybackCommand.Pause), "Commands remain usable after a failed native command.");
    await Until(() => fixture.Interaction.Snapshot.State == PlayerState.Paused);
    await plugin.ShutdownAsync(CancellationToken.None);
    var updateCount = plugin.UpdateCount;
    await fixture.Session.ClearQueueAsync();
    Check(plugin.UpdateCount == updateCount && plugin.Cleaned && !plugin.Request(PlaybackCommand.Play), "Shutdown must unsubscribe, stop workers, and reject native callbacks.");
    await plugin.ShutdownAsync(CancellationToken.None);
    fixture.Interaction.Dispose();
    await Throws<ObjectDisposedException>(() => fixture.Interaction.ExecuteAsync(PlaybackCommand.Play));
}

static async Task Platforms()
{
    var root = Path.Combine(Path.GetTempPath(), "lemonneo-interactive-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var platform = OperatingSystem.IsWindows() ? "macos" : "windows";
        await File.WriteAllTextAsync(Path.Combine(root, "plugin.json"), $$"""
            { "id": "foreign", "name": "Foreign", "version": "0.1.0", "minimumHostVersion": "0.1.0", "entryAssembly": "missing.dll", "platforms": ["{{platform}}"] }
            """);
        await using var loader = new PluginLoader(new ProviderRegistry(), new PlayerRegistry([]), NullLoggerFactory.Instance,
            new TestHttpClientFactory(), new TestDataStore(root), NullLogger<PluginLoader>.Instance);
        var report = await loader.LoadFromDirectoryAsync(root);
        Check(report.Loaded.Count == 0 && report.Failures.Count == 0, "Foreign plugins must be skipped before assembly resolution.");
        await File.WriteAllTextAsync(Path.Combine(root, "plugin.json"), "{ \"id\": \"bad\", \"name\": \"Bad\", \"version\": \"0.1.0\", \"minimumHostVersion\": \"0.1.0\", \"entryAssembly\": \"missing.dll\", \"platforms\": [\"typo\"] }");
        report = await loader.LoadFromDirectoryAsync(root);
        Check(report.Failures.Count == 1, "Misspelled platform names must fail validation.");
    }
    finally { Directory.Delete(root, recursive: true); }
}

static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
static async Task Until(Func<bool> condition)
{
    var end = DateTime.UtcNow.AddSeconds(5);
    while (!condition())
    {
        if (DateTime.UtcNow >= end) throw new TimeoutException("Interactive state did not converge.");
        await Task.Delay(25);
    }
}

internal sealed class Fixture : IAsyncDisposable
{
    public static Track First { get; } = new() { Id = new("library", "1"), Title = "Interactive test track", Artists = [new() { Name = "Test Artist" }], Album = new() { Title = "Test Album" }, Duration = TimeSpan.FromSeconds(60), ArtworkUri = new("https://example.com/cover.jpg") };
    public static Track Second { get; } = First with { Id = new("library", "2"), Title = "Second track", ArtworkUri = null };
    public TestPlayer Player { get; } = new();
    public TestRepository Repository { get; }
    public PlaybackSessionService Session { get; }
    public PlaybackInteractionService Interaction { get; }
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "lemonneo-interactive-" + Guid.NewGuid().ToString("N"));

    private Fixture(PlaybackQueueState? state, bool seekable)
    {
        Repository = new TestRepository(state ?? new());
        var providers = new TestProviders();
        var resolver = new PlaybackResolver(providers, providers, new PlayerRegistry([new TestPlayerProvider(seekable ? Player : new NonSeekablePlayer(Player))]),
            new PlaybackSourceDiscoveryService(providers, new MetadataService(providers)), new PlaybackAudioCache(_cache, NullLogger<PlaybackAudioCache>.Instance));
        Session = new(resolver, new PlaybackQueue(), Repository, NullLogger<PlaybackSessionService>.Instance);
        Interaction = new(Session, NullLogger<PlaybackInteractionService>.Instance);
    }
    public static async Task<Fixture> Create(PlaybackQueueState? state = null, bool seekable = true)
    {
        var fixture = new Fixture(state, seekable);
        await fixture.Session.InitializeAsync();
        return fixture;
    }
    public async ValueTask DisposeAsync()
    {
        Interaction.Dispose();
        await Session.DisposeAsync();
        if (Directory.Exists(_cache)) Directory.Delete(_cache, recursive: true);
    }
}

internal sealed class TestRepository(PlaybackQueueState saved) : IPlaybackStateRepository
{
    public PlaybackQueueState Saved { get; private set; } = saved;
    public Task<PlaybackQueueState> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Saved);
    public Task SaveAsync(PlaybackQueueState state, CancellationToken cancellationToken = default) { Saved = state; return Task.CompletedTask; }
}

internal sealed class TestPlayer : ISeekablePlayer
{
    public PlayerState State { get; private set; }
    public Track? CurrentTrack { get; private set; }
    public TimeSpan Position { get; private set; }
    public bool HasEnded { get; set; }
    public int PlayCount { get; private set; }
    public PlayerTimeline Timeline => new(Position, CurrentTrack?.Duration, HasEnded);
    public Task PlayAsync(Track track, PlaybackSource source, CancellationToken cancellationToken = default) { CurrentTrack = track with { Id = new("decoder", track.Id.RemoteId), Title = "Decoder metadata" }; Position = TimeSpan.Zero; State = PlayerState.Playing; HasEnded = false; PlayCount++; return Task.CompletedTask; }
    public Task PauseAsync(CancellationToken cancellationToken = default) { State = PlayerState.Paused; return Task.CompletedTask; }
    public Task ResumeAsync(CancellationToken cancellationToken = default) { State = PlayerState.Playing; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken = default) { State = PlayerState.Stopped; Position = TimeSpan.Zero; CurrentTrack = null; HasEnded = false; return Task.CompletedTask; }
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) { Position = position; return Task.CompletedTask; }
}
internal sealed class NonSeekablePlayer(TestPlayer player) : IPlayer
{
    public PlayerState State => player.State;
    public Track? CurrentTrack => player.CurrentTrack;
    public PlayerTimeline Timeline => player.Timeline;
    public Task PlayAsync(Track track, PlaybackSource source, CancellationToken cancellationToken = default) => player.PlayAsync(track, source, cancellationToken);
    public Task PauseAsync(CancellationToken cancellationToken = default) => player.PauseAsync(cancellationToken);
    public Task ResumeAsync(CancellationToken cancellationToken = default) => player.ResumeAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken = default) => player.StopAsync(cancellationToken);
}
internal sealed class TestPlayerProvider(IPlayer player) : IPlayerProvider
{
    public string Id => "test-player";
    public PlayerDescriptor Descriptor => new("Test", "Silent fixture");
    public int Priority => 1;
    public IPlayer Player => player;
    public bool CanPlay(PlaybackSource source) => true;
}
internal sealed class TestProviders : IProviderRegistry, IPlaybackSourceProviderRegistry, IMusicProvider, IPlaybackProvider
{
    public string Id => "library";
    public ProviderDescriptor Descriptor => new("Test", "Offline fixture");
    public IReadOnlyCollection<IMusicProvider> Providers => [this];
    public IReadOnlyCollection<PlaybackSourceProviderRegistration> Sources => [new(Id, this, this, [])];
    public IMusicProvider? Get(string providerId) => providerId == Id ? this : null;
    PlaybackSourceProviderRegistration? IPlaybackSourceProviderRegistry.Get(string providerId) => providerId == Id ? new(Id, this, this, []) : null;
    public Task<PlaybackSource?> ResolvePlaybackAsync(Track track, PlaybackRequest request, CancellationToken cancellationToken = default) => Task.FromResult<PlaybackSource?>(new FilePlaybackSource("fixture.audio"));
}

internal sealed class ProbePlugin : InteractivePluginBase
{
    public override PluginDescriptor Descriptor => new("probe", "Probe", "0.1.0");
    public InteractivePlaybackSnapshot? Last { get; private set; }
    public int UpdateCount { get; private set; }
    public bool Cleaned { get; private set; }
    public bool FailNextUpdate { get; set; }
    public bool Request(PlaybackCommand command) => RequestCommand(command);
    protected override ValueTask InitializeCoreAsync(IPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    protected override ValueTask UpdateAsync(InteractivePlaybackSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (FailNextUpdate) { FailNextUpdate = false; throw new InvalidOperationException("Update failed"); }
        UpdateCount++; Last = snapshot; return ValueTask.CompletedTask;
    }
    protected override ValueTask ShutdownCoreAsync() { Cleaned = true; return ValueTask.CompletedTask; }
}
internal sealed class TestContext(IPlaybackInteraction playback) : IPluginContext
{
    public IPlaybackInteraction Playback => playback;
    public ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;
    public IHttpClientFactory HttpClientFactory => new TestHttpClientFactory();
    public IPluginStorage Storage => throw new NotSupportedException();
    public ICredentialVault Credentials => throw new NotSupportedException();
    public string CacheDirectory => Path.GetTempPath();
    public void RegisterProvider(IMusicProvider provider) => throw new NotSupportedException();
    public void RegisterPlayer(IPlayerProvider player) => throw new NotSupportedException();
}
internal sealed class TestHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
internal sealed class TestDataStore(string root) : IPluginDataStoreFactory
{
    public IPluginStorage CreateStorage(string pluginId) => throw new NotSupportedException();
    public ICredentialVault CreateCredentialVault(string pluginId) => throw new NotSupportedException();
    public string GetCacheDirectory(string pluginId) => root;
}
