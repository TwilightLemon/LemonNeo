using LemonNeo.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Configuration;

/// <summary>Owns package entities and one periodic writer. UI updates never wait for persistence.</summary>
public sealed class AppSettingsService(
    ISettingsEntityStore store,
    ILogger<AppSettingsService> logger,
    TimeProvider? timeProvider = null,
    TimeSpan? saveInterval = null) : IAppSettingsService, IAsyncDisposable
{
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Dictionary<string, Scope> _scopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _timerCancellation = new();
    private Task _timerTask = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _started;
    private bool _closing;

    public ISettingsScope ForPackage(string packageId)
    {
        PackageNames.Validate(packageId);
        lock (_stateGate)
        {
            ThrowIfClosing();
            if (!_scopes.TryGetValue(packageId, out var scope))
                _scopes.Add(packageId, scope = new(this, packageId));
            if (scope.PackageId != packageId)
                throw new ArgumentException("Package name casing must match its registration.", nameof(packageId));
            return scope;
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Entry[] entries;
        lock (_stateGate) entries = _scopes.Values.SelectMany(scope => scope.Entries.Values).ToArray();
        foreach (var entry in entries) await LoadEntryAsync(entry, cancellationToken);
    }

    private async Task LoadEntryAsync(Entry entry, CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken);
        Action? notify = null;
        try
        {
            lock (_stateGate)
            {
                ThrowIfClosing();
                if (entry.Loaded) return;
                entry.Scope.ThrowIfReleased();
            }
            var value = await entry.ReadAsync(store, cancellationToken);
            lock (_stateGate)
            {
                entry.Scope.ThrowIfReleased();
                notify = entry.Load(value);
            }
        }
        finally { _loadGate.Release(); }
        notify?.Invoke();
    }

    public void Start()
    {
        lock (_stateGate)
        {
            ThrowIfClosing();
            if (_started) return;
            _started = true;
            _timerTask = RunTimerAsync(_timerCancellation.Token);
        }
    }

    private async Task RunTimerAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(saveInterval ?? TimeSpan.FromSeconds(10), timeProvider ?? TimeProvider.System);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try { await SaveAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogWarning(exception, "Periodic configuration save failed; changed entities will be retried."); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async Task StopAsync()
    {
        await _timerCancellation.CancelAsync();
        await _timerTask;
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try { await SaveEntriesAsync(null, cancellationToken); }
        finally { _saveGate.Release(); }
    }

    private async Task SaveEntriesAsync(Scope? scope, CancellationToken cancellationToken)
    {
        (Entry Entry, object Value, long Version)[] snapshots;
        lock (_stateGate)
        {
            IEnumerable<Scope> scopes = scope is null ? _scopes.Values : [scope];
            snapshots = scopes
                .SelectMany(item => item.Entries.Values)
                .Where(entry => entry.Loaded && entry.Version != entry.SavedVersion)
                .Select(entry => (entry, entry.Value, entry.Version)).ToArray();
        }
        List<Exception>? errors = null;
        foreach (var (entry, value, version) in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await entry.WriteAsync(store, value, cancellationToken);
                lock (_stateGate) entry.SavedVersion = version;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                (errors ??= []).Add(new IOException($"Could not save {entry.Scope.PackageId}/{entry.Name}.", exception));
            }
        }
        if (errors is not null) throw new AggregateException("Configuration save failed.", errors);
    }

    public async Task ReleasePackageAsync(string packageId, CancellationToken cancellationToken = default)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            await _saveGate.WaitAsync(cancellationToken);
            try
            {
                Scope? scope;
                lock (_stateGate) _scopes.TryGetValue(packageId, out scope);
                if (scope is null) return;
                lock (_stateGate) scope.Releasing = true;
                try { await SaveEntriesAsync(scope, cancellationToken); }
                catch
                {
                    lock (_stateGate) scope.Releasing = false;
                    throw;
                }
                lock (_stateGate)
                {
                    scope.Released = true;
                    scope.Entries.Clear();
                    _scopes.Remove(packageId);
                }
            }
            finally { _saveGate.Release(); }
        }
        finally { _loadGate.Release(); }
    }

    private void ThrowIfClosing() => ObjectDisposedException.ThrowIf(_closing, this);

    public ValueTask DisposeAsync()
    {
        lock (_stateGate) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        lock (_stateGate) _closing = true;
        try
        {
            await StopAsync();
            await SaveAsync();
        }
        finally
        {
            lock (_stateGate)
            {
                foreach (var scope in _scopes.Values) { scope.Released = true; scope.Entries.Clear(); }
                _scopes.Clear();
            }
            _timerCancellation.Dispose();
        }
    }

    private sealed class Scope(AppSettingsService owner, string packageId) : ISettingsScope
    {
        public string PackageId { get; } = packageId;
        public Dictionary<Type, Entry> Entries { get; } = [];
        public bool Released { get; set; }
        public bool Releasing { get; set; }
        public void ThrowIfReleased() => ObjectDisposedException.ThrowIf(Released || Releasing, this);

        public void Register<T>(string entityName, T defaults, Func<T, T>? normalize = null) where T : class
        {
            ArgumentNullException.ThrowIfNull(defaults);
            if (!PackageNames.IsIdentifier(entityName)) throw new ArgumentException("Entity names must be PascalCase identifiers.", nameof(entityName));
            lock (owner._stateGate)
            {
                owner.ThrowIfClosing();
                ThrowIfReleased();
                if (Entries.TryGetValue(typeof(T), out var existing))
                {
                    if (existing.Name != entityName) throw new InvalidOperationException($"Type {typeof(T).Name} is already registered.");
                    return;
                }
                if (Entries.Values.Any(entry => string.Equals(entry.Name, entityName, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"Entity {entityName} is already registered.");
                Entries.Add(typeof(T), new Entry<T>(this, entityName, defaults, normalize));
            }
        }

        public async Task RegisterAsync<T>(string entityName, T defaults, Func<T, T>? normalize = null,
            CancellationToken cancellationToken = default) where T : class
        {
            Register(entityName, defaults, normalize);
            Entry entry;
            lock (owner._stateGate) entry = Entries[typeof(T)];
            await owner.LoadEntryAsync(entry, cancellationToken);
        }

        private Entry<T> GetEntry<T>() where T : class
        {
            owner.ThrowIfClosing();
            ThrowIfReleased();
            return Entries.TryGetValue(typeof(T), out var entry) ? (Entry<T>)entry
                : throw new InvalidOperationException($"Configuration {typeof(T).Name} has not been registered in {PackageId}.");
        }

        public T Get<T>() where T : class
        {
            lock (owner._stateGate) return (T)GetEntry<T>().Value;
        }

        public void Apply<T>(Func<T, T> update) where T : class
        {
            Action? notify;
            lock (owner._stateGate)
            {
                var entry = GetEntry<T>();
                if (!entry.Loaded) throw new InvalidOperationException($"Configuration {entry.Name} has not finished loading.");
                notify = entry.Update(update);
            }
            notify?.Invoke();
        }

        public IDisposable Subscribe<T>(Action<T> onChanged) where T : class
        {
            ArgumentNullException.ThrowIfNull(onChanged);
            lock (owner._stateGate)
            {
                var entry = GetEntry<T>();
                entry.Listeners.Add(onChanged);
                return new Subscription(() => { lock (owner._stateGate) entry.Listeners.Remove(onChanged); });
            }
        }
    }

    private abstract class Entry(Scope scope, string name)
    {
        public Scope Scope { get; } = scope;
        public string Name { get; } = name;
        public bool Loaded { get; protected set; }
        public long Version { get; protected set; }
        public long SavedVersion { get; set; }
        public abstract object Value { get; }
        public abstract Task<object> ReadAsync(ISettingsEntityStore store, CancellationToken cancellationToken);
        public abstract Task WriteAsync(ISettingsEntityStore store, object value, CancellationToken cancellationToken);
        public abstract Action? Load(object value);
    }

    private sealed class Entry<T>(Scope scope, string name, T defaults, Func<T, T>? normalize) : Entry(scope, name) where T : class
    {
        private T _current = normalize is null ? defaults : normalize(defaults);
        public List<Action<T>> Listeners { get; } = [];
        public override object Value => _current;
        public override async Task<object> ReadAsync(ISettingsEntityStore store, CancellationToken cancellationToken) =>
            await store.LoadAsync<T>(Scope.PackageId, Name, cancellationToken) ?? _current;
        public override Task WriteAsync(ISettingsEntityStore store, object value, CancellationToken cancellationToken) =>
            store.SaveAsync(Scope.PackageId, Name, (T)value, cancellationToken);
        public override Action? Load(object value)
        {
            var next = Normalize((T)value);
            var changed = !EqualityComparer<T>.Default.Equals(_current, next);
            _current = next;
            Loaded = true;
            return changed ? Notification(next) : null;
        }
        public Action? Update(Func<T, T> update)
        {
            var next = Normalize(update(_current));
            if (EqualityComparer<T>.Default.Equals(_current, next)) return null;
            _current = next;
            Version++;
            return Notification(next);
        }
        private T Normalize(T value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var result = normalize is null ? value : normalize(value);
            ArgumentNullException.ThrowIfNull(result);
            return result;
        }
        private Action Notification(T value)
        {
            var listeners = Listeners.ToArray();
            return () => { foreach (var listener in listeners) listener(value); };
        }
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;
        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
