using System.Collections.Concurrent;
using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Abstractions.Storage;
using LemonNeo.Application;
using LemonNeo.Application.Library;
using LemonNeo.Application.Lyrics;
using LemonNeo.Application.Playback;
using LemonNeo.Application.Providers;
using LemonNeo.Core.Exceptions;
using LemonNeo.Core.Library;
using LemonNeo.Core.Lyrics;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace LemonNeo.Tests;

internal sealed class PlaybackFixture : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LemonNeo.Tests", Guid.NewGuid().ToString("N"));
    public TestProvider Provider { get; } = new();
    public TestPlayer Player { get; } = new();
    public TestRegistry Registry { get; }
    public MemorySettings Settings { get; } = new();
    public PlaybackSessionService Session { get; }
    public PlaybackLyricsService Lyrics { get; }
    public PlaybackInteractionService Interaction { get; }

    public PlaybackFixture()
    {
        var registry = Registry = new TestRegistry(Provider, Player);
        var audio = new PlaybackAudioCache(() => _directory, NullLogger<PlaybackAudioCache>.Instance);
        var library = new LibraryService(new MemoryLibrary());
        var discovery = new PlaybackSourceDiscoveryService(registry, new MetadataService(registry), audio);
        var resolver = new PlaybackResolver(registry, registry, registry, discovery, audio, library,
            new PlaylistService(library, new FractionalPlaylistOrderingService()));
        Session = new PlaybackSessionService(resolver, new PlaybackQueue(), Settings,
            NullLogger<PlaybackSessionService>.Instance);
        Lyrics = new PlaybackLyricsService(Session, new LyricsService(registry));
        Interaction = new PlaybackInteractionService(Session, NullLogger<PlaybackInteractionService>.Instance);
    }

    public Task InitializeAsync() => Session.InitializeAsync();
    public static Track Track(string id) => new() { Id = new MusicItemId("test", id), Title = id, Duration = TimeSpan.FromMinutes(3) };
    public static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await Session.PrepareForShutdownAsync();
        await Lyrics.DisposeAsync();
        Interaction.Dispose();
        await Session.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

internal sealed class TestProvider : IMusicProvider, IPlaybackProvider, ILyricsProvider
{
    public string Id => "test";
    public ProviderDescriptor Descriptor => new("Test", "Controlled playback and lyrics");
    public IReadOnlyCollection<MusicIdScheme> AcceptedIdSchemes => [new("test", "track-id")];
    public ConcurrentQueue<string> Sources { get; } = new();
    public ConcurrentQueue<string> Lyrics { get; } = new();
    public Func<MusicItemId, CancellationToken, Task<PlaybackSource?>>? Resolve { get; set; }
    public Func<Track, CancellationToken, Task<Lyrics?>>? FetchLyrics { get; set; }

    public Task<PlaybackSource?> ResolvePlaybackAsync(MusicItemId trackId, PlaybackRequest request, CancellationToken cancellationToken = default)
    {
        Sources.Enqueue(trackId.RemoteId);
        return Resolve?.Invoke(trackId, cancellationToken) ?? Task.FromResult<PlaybackSource?>(
            new HttpPlaybackSource { Uri = new Uri($"https://test.invalid/{trackId.RemoteId}"), Quality = AudioQuality.HiRes });
    }

    public Task<Lyrics?> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        Lyrics.Enqueue(track.Id.RemoteId);
        return FetchLyrics?.Invoke(track, cancellationToken) ?? Task.FromResult<Lyrics?>(new Lyrics(track.Title));
    }
}

internal sealed class TestPlayer : IPlayer, IPlayerProvider
{
    private TestPrepared? _active;
    public string Id => "test-player";
    public PlayerDescriptor Descriptor => new("Test", "Controlled decoder");
    public IReadOnlyCollection<string> SupportedExtensions => [".wav"];
    public IPlayer Player => this;
    public bool CanPlay(PlaybackSource source) => true;
    public PlayerState State { get; private set; }
    public Track? CurrentTrack => _active?.Track;
    public PlayerTimeline Timeline { get; set; } = new(TimeSpan.Zero, TimeSpan.FromMinutes(3));
    public double Volume { get; private set; } = 1;
    public ConcurrentQueue<string> Activations { get; } = new();
    public Func<Track, CancellationToken, Task<IPreparedPlayback>>? Prepare { get; set; }

    public Task<IPreparedPlayback> PrepareAsync(Track track, PlaybackSource source, IPlaybackDownloadSink? downloadSink = null,
        CancellationToken cancellationToken = default) => Prepare?.Invoke(track, cancellationToken)
        ?? Task.FromResult<IPreparedPlayback>(new TestPrepared(this, track));
    public Task PlayAsync(Track track, PlaybackSource source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task PauseAsync(CancellationToken cancellationToken = default) { State = PlayerState.Paused; return Task.CompletedTask; }
    public Task ResumeAsync(CancellationToken cancellationToken = default) { State = PlayerState.Playing; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken = default) { _active?.Dispose(); return Task.CompletedTask; }
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) { Timeline = Timeline with { Position = position }; return Task.CompletedTask; }
    public Task<double> GetVolumeAsync(CancellationToken cancellationToken = default) => Task.FromResult(Volume);
    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) { Volume = volume; return Task.CompletedTask; }

    public sealed class TestPrepared(TestPlayer owner, Track track) : IPreparedPlayback
    {
        public Track Track { get; } = track;
        public bool Disposed { get; private set; }
        public AudioStreamInfo? AudioInfo => null;
        public void Activate(double volume)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            owner._active = this;
            owner.State = PlayerState.Playing;
            owner.Timeline = new PlayerTimeline(TimeSpan.Zero, Track.Duration);
            owner.Volume = volume;
            owner.Activations.Enqueue(Track.Id.RemoteId);
        }
        public void Dispose()
        {
            Disposed = true;
            if (ReferenceEquals(owner._active, this)) { owner._active = null; owner.State = PlayerState.Stopped; }
        }
    }
}

internal sealed class TestRegistry(TestProvider provider, TestPlayer player) : IProviderRegistry, IPlaybackSourceProviderRegistry, IPlayerRegistry
{
    public IReadOnlyCollection<IMusicProvider> Providers => [provider];
    public IReadOnlyCollection<PlaybackSourceProviderRegistration> Sources => [Registration];
    public IReadOnlyCollection<IPlayerProvider> Players => [player];
    public bool HasDecoder { get; set; } = true;
    private PlaybackSourceProviderRegistration Registration => new(provider.Id, provider, provider, provider.AcceptedIdSchemes.ToArray());
    IMusicProvider? IProviderRegistry.Get(string id) => id == provider.Id ? provider : null;
    PlaybackSourceProviderRegistration? IPlaybackSourceProviderRegistry.Get(string id) => id == provider.Id ? Registration : null;
    IPlayerProvider? IPlayerRegistry.Get(string id) => id == player.Id ? player : null;
    public IPlayerProvider GetRequiredPlayer(PlaybackSource source, string? preferredPlayerId = null) => HasDecoder
        ? player : throw new PlaybackResolutionException("No decoder.");
}

internal sealed class MemoryLibrary : ILibraryRepository
{
    public Task EnsureCreatedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<MusicLibrary> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MusicLibrary());
    public Task SaveAsync(LibrarySaveSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class MemorySettings : IAppSettingsService, ISettingsScope
{
    private readonly ConcurrentDictionary<Type, object> _values = new();
    public string PackageId => PackageNames.Application;
    public ISettingsScope ForPackage(string packageId) => this;
    public void Register<T>(string entityName, T defaults, Func<T, T>? normalize = null) where T : class =>
        _values.AddOrUpdate(typeof(T), normalize?.Invoke(defaults) ?? defaults,
            (_, value) => normalize?.Invoke((T)value) ?? value);
    public Task RegisterAsync<T>(string entityName, T defaults, Func<T, T>? normalize = null, CancellationToken cancellationToken = default) where T : class
    { Register(entityName, defaults, normalize); return Task.CompletedTask; }
    public T Get<T>() where T : class => (T)_values[typeof(T)];
    public void Apply<T>(Func<T, T> update) where T : class => _values.AddOrUpdate(typeof(T), _ => throw new InvalidOperationException(), (_, value) => update((T)value));
    public IDisposable Subscribe<T>(Action<T> onChanged) where T : class => new Subscription();
    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Start() { }
    public Task StopAsync() => Task.CompletedTask;
    public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReleasePackageAsync(string packageId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    private sealed class Subscription : IDisposable { public void Dispose() { } }
}
