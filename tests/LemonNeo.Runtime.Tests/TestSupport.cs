using System.Collections.Concurrent;
using System.Text.Json;
using LemonNeo.Configuration;
using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;
using LemonNeo.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace LemonNeo.Runtime.Tests;

internal sealed class TemporaryPaths : IAppPathProvider, IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "LemonNeo.Runtime.Tests", Guid.NewGuid().ToString("N"));
    public string ConfigurationDirectory => Path.Combine(Root, "Configuration");
    public string DataDirectory => Path.Combine(Root, "Data");
    public string CacheDirectory => Path.Combine(Root, "Cache");
    public string MusicLibraryDirectory => Path.Combine(Root, "Library");
    public string DataProtectionKeysDirectory => Path.Combine(DataDirectory, "Keys");
    public string PluginDirectory => Path.Combine(Root, "Plugins");
    public TemporaryPaths() => Directory.CreateDirectory(Root);
    public string GetPackageConfigurationDirectory(string packageId)
    {
        PackageNames.Validate(packageId);
        return Path.Combine(ConfigurationDirectory, packageId);
    }
    public string GetEntityConfigurationFile(string packageId, string entityName)
    {
        if (!PackageNames.IsIdentifier(entityName)) throw new ArgumentException("Invalid entity.");
        return Path.Combine(GetPackageConfigurationDirectory(packageId), entityName + ".json");
    }
    public string GetPluginCredentialDirectory(string pluginId) => Path.Combine(DataDirectory, "Credentials", pluginId);
    public string GetPluginCacheDirectory(string pluginId) => Path.Combine(CacheDirectory, pluginId);
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
}

internal sealed class WriteBlock
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class RecordingStore(ISettingsEntityStore? inner = null) : ISettingsEntityStore
{
    private readonly ConcurrentDictionary<string, string> _values = new();
    public ConcurrentQueue<string> Writes { get; } = new();
    public ConcurrentQueue<string> Reads { get; } = new();
    public string? FailEntity { get; set; }
    public string? BlockEntity { get; set; }
    public WriteBlock? Block { get; set; }
    public TaskCompletionSource FirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource FailedWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public T? Saved<T>(string packageId, string entityName) => _values.TryGetValue(packageId + "/" + entityName, out var json)
        ? JsonSerializer.Deserialize<T>(json) : default;
    public int WriteCount(string entityName) => Writes.Count(path => path.EndsWith("/" + entityName, StringComparison.Ordinal));
    public async Task<T?> LoadAsync<T>(string packageId, string entityName, CancellationToken cancellationToken = default) where T : class
    {
        Reads.Enqueue(packageId + "/" + entityName);
        if (inner is not null) return await inner.LoadAsync<T>(packageId, entityName, cancellationToken);
        return Saved<T>(packageId, entityName);
    }
    public async Task SaveAsync<T>(string packageId, string entityName, T value, CancellationToken cancellationToken = default) where T : class
    {
        var json = JsonSerializer.Serialize(value);
        if (entityName == FailEntity)
        {
            FailedWrite.TrySetResult();
            throw new IOException("Acceptance test write failure.");
        }
        if (entityName == BlockEntity && Block is { } block)
        {
            BlockEntity = null;
            block.Entered.TrySetResult();
            await block.Release.Task.WaitAsync(cancellationToken);
        }
        if (inner is not null) await inner.SaveAsync(packageId, entityName, value, cancellationToken);
        _values[packageId + "/" + entityName] = json;
        Writes.Enqueue(packageId + "/" + entityName);
        FirstWrite.TrySetResult();
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_timers) { _timers.Add(timer); timer.Change(dueTime, period); }
        return timer;
    }
    public void Advance(TimeSpan elapsed)
    {
        List<Action> callbacks = [];
        lock (_timers)
        {
            _ticks += elapsed.Ticks;
            foreach (var timer in _timers)
                while (!timer.Disposed && timer.Due <= _ticks)
                {
                    callbacks.Add(timer.Fire);
                    timer.Due = timer.Period > 0 ? timer.Due + timer.Period : long.MaxValue;
                }
        }
        foreach (var callback in callbacks) callback();
    }
    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public bool Disposed { get; private set; }
        public long Due { get; set; } = long.MaxValue;
        public long Period { get; private set; }
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._timers)
            {
                if (Disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                Period = period.Ticks;
                return true;
            }
        }
        public void Dispose() { lock (owner._timers) Disposed = true; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class TestPlayer : IPlayerProvider, ISeekablePlayer, IVolumePlayer
{
    public string Id => "acceptance-player";
    public PlayerDescriptor Descriptor { get; } = new("Acceptance player", "In-memory playback.");
    public int Priority => 100;
    public IPlayer Player => this;
    public PlayerState State { get; private set; }
    public Track? CurrentTrack { get; private set; }
    public PlayerTimeline Timeline { get; private set; } = new(TimeSpan.Zero, null);
    public double Volume { get; private set; } = 1;
    public int PlayCalls { get; private set; }
    public bool CanPlay(PlaybackSource source) => true;
    public Task PlayAsync(Track track, PlaybackSource source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PlayCalls++;
        CurrentTrack = track;
        Timeline = new(TimeSpan.Zero, track.Duration);
        State = PlayerState.Playing;
        return Task.CompletedTask;
    }
    public Task PauseAsync(CancellationToken cancellationToken = default) { State = PlayerState.Paused; return Task.CompletedTask; }
    public Task ResumeAsync(CancellationToken cancellationToken = default) { State = PlayerState.Playing; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        State = PlayerState.Stopped;
        Timeline = new(TimeSpan.Zero, CurrentTrack?.Duration);
        return Task.CompletedTask;
    }
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        Timeline = Timeline with { Position = position };
        return Task.CompletedTask;
    }
    public Task<double> GetVolumeAsync(CancellationToken cancellationToken = default) => Task.FromResult(Volume);
    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) { Volume = volume; return Task.CompletedTask; }
}

internal sealed class TestMusic : IMusicProvider, IPlaybackProvider
{
    public string Id => "acceptance-music";
    public ProviderDescriptor Descriptor { get; } = new("Acceptance music", "No network resolution.");
    public Task<PlaybackSource?> ResolvePlaybackAsync(Track track, PlaybackRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult<PlaybackSource?>(new FilePlaybackSource("acceptance.audio"));
}

internal static class RuntimeFixture
{
    public static Track Track(string id = "one") => new()
    {
        Id = new("acceptance-music", id), Title = id, Duration = TimeSpan.FromMinutes(3)
    };
    public static AppRuntime Create(TemporaryPaths paths, TestPlayer player, RecordingStore? store = null) =>
        new(paths, new(paths.PluginDirectory, paths.MusicLibraryDirectory), services =>
        {
            services.AddSingleton<IPlayerProvider>(player);
            services.AddSingleton<IMusicProvider>(new TestMusic());
            if (store is not null) services.AddSingleton<ISettingsEntityStore>(store);
        });
}
