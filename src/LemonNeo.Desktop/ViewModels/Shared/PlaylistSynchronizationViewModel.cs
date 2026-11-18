using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Application.Library;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.ViewModels.Shared;

/// <summary>Owns the desktop sync lifetime; all service calls and notifications stay on the UI context.</summary>
public sealed class PlaylistSynchronizationViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DesktopContext _context;
    private readonly PlaylistSyncService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(15) };
    private Task _operation = Task.CompletedTask;
    private bool _isSyncing;
    private bool _started;
    private bool _closing;
    private string? _error;

    public PlaylistSynchronizationViewModel(DesktopContext context)
    {
        _context = context;
        _service = context.Host.Get<PlaylistSyncService>();
        SyncAllCommand = new AsyncRelayCommand(() => SynchronizeAsync(), () => CanSync);
        TogglePauseAllCommand = new AsyncRelayCommand(() => SetPausedAsync(null, !IsPaused), () => HasMirrors);
        _timer.Tick += OnTimer;
        _service.Changed += OnChanged;
        context.LibraryChanged += OnLibraryChanged;
        context.Text.Changed += OnLanguageChanged;
    }

    public bool IsSyncing { get => _isSyncing; private set => SetProperty(ref _isSyncing, value); }
    public bool HasMirrors => _context.Host.Get<LibraryService>().Current.Mirrors.Count > 0;
    public bool IsPaused => _context.Host.Get<LibraryService>().Current.IsPlaylistSyncPaused;
    public bool IsAutomaticSyncEnabled
    {
        get => !IsPaused;
        set
        {
            if (value != IsAutomaticSyncEnabled && CanChangeGlobalPause) _ = SetPausedAsync(null, !value);
        }
    }
    private bool _isChangingGlobalPause;
    public bool CanChangeGlobalPause => _context.IsInitialized && HasMirrors && !_closing && !_isChangingGlobalPause;
    public bool CanSync => _context.IsInitialized && HasMirrors && !IsSyncing && !_closing;
    public string PauseAllLabel => _context.Text[IsPaused ? "ResumeAllPlaylistSync" : "PauseAllPlaylistSync"];
    public string? Error { get => _error; private set => SetProperty(ref _error, value); }
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public AsyncRelayCommand SyncAllCommand { get; }
    public AsyncRelayCommand TogglePauseAllCommand { get; }

    public void Start()
    {
        if (_started || _closing) return;
        _started = true;
        _timer.Start();
        _ = SynchronizeAsync(automatic: true);
    }

    public Task SynchronizeAsync(Guid? playlistId = null, bool automatic = false)
    {
        if (!CanSync || automatic && IsPaused) return Task.CompletedTask;
        IsSyncing = true;
        Refresh();
        _operation = RunAsync(playlistId, automatic);
        return _operation;
    }

    private async Task RunAsync(Guid? playlistId, bool automatic)
    {
        Error = null;
        try
        {
            var results = playlistId is { } id
                ? new[] { await _service.SynchronizeAsync(id, automatic, _lifetime.Token) }
                : await _service.SynchronizeAllAsync(automatic, _lifetime.Token);
            Error = string.Join(Environment.NewLine, results.Where(result => result.Error is not null)
                .Select(result => $"{_context.Playlists.FirstOrDefault(playlist => playlist.Id == result.PlaylistId)?.Name}: {result.Error}"));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { Error = exception.Message; }
        finally
        {
            IsSyncing = false;
            Refresh();
        }
    }

    public async Task SetPausedAsync(Guid? playlistId, bool paused)
    {
        if (_closing) return;
        if (playlistId is null)
        {
            if (!CanChangeGlobalPause) return;
            _isChangingGlobalPause = true;
            OnPropertyChanged(nameof(CanChangeGlobalPause));
        }
        try
        {
            await _service.SetPausedAsync(playlistId, paused, _lifetime.Token);
            Error = null;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { Error = exception.Message; }
        finally { if (playlistId is null) _isChangingGlobalPause = false; }
        Refresh();
    }

    private void OnTimer(object? sender, EventArgs args) => _ = SynchronizeAsync(automatic: true);
    private void OnChanged(object? sender, EventArgs args) => _context.RefreshLibrary();
    private void OnLibraryChanged(object? sender, EventArgs args) => Refresh();
    private void OnLanguageChanged(object? sender, EventArgs args) => Refresh();

    private void Refresh()
    {
        OnPropertyChanged(nameof(HasMirrors));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsAutomaticSyncEnabled));
        OnPropertyChanged(nameof(CanChangeGlobalPause));
        OnPropertyChanged(nameof(CanSync));
        OnPropertyChanged(nameof(PauseAllLabel));
        OnPropertyChanged(nameof(HasError));
        SyncAllCommand.NotifyCanExecuteChanged();
        TogglePauseAllCommand.NotifyCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        _closing = true;
        _timer.Stop();
        _timer.Tick -= OnTimer;
        await _lifetime.CancelAsync();
        await _operation;
        _service.Changed -= OnChanged;
        _context.LibraryChanged -= OnLibraryChanged;
        _context.Text.Changed -= OnLanguageChanged;
        _lifetime.Dispose();
    }
}
