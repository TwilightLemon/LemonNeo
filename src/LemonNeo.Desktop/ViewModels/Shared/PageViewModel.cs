using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Irihi.Lingua;
using LemonNeo.Application.Library;
using LemonNeo.Core.Library;
using LemonNeo.Core.Music;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.ViewModels.Shared;

public abstract class PageViewModel : ObservableObject, IAsyncDisposable
{
    protected readonly DesktopContext Context;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _closing;
    private bool _isBusy;
    private bool _isNavigationActive = true;
    private bool _localizationDirty;
    private Task _operation = Task.CompletedTask;
    private CancellationTokenSource? _operationCancellation;
    private LinguaKey? _statusKey;

    protected PageViewModel(DesktopContext context, string route)
    {
        Context = context;
        Route = route;
        CancelOperationCommand =
            new RelayCommand(() => _operationCancellation?.Cancel(), () => _operationCancellation is not null);
        Context.PropertyChanged += OnContextChanged;
        _text.Changed += OnLanguageChanged;
    }

    protected DesktopHost _host => Context.Host;
    protected LocalizationService _text => Context.Text;
    protected CancellationToken LifetimeToken => _lifetime.Token;

    public string Route { get; }
    public virtual string PageTitle => _text[Route];
    public virtual string PageDescription => _text[$"{Route}Description"];
    public virtual string? PageSummary => null;
    public IReadOnlyList<Playlist> EditablePlaylists => Context.Playlists.Where(playlist =>
        !_host.Get<PlaylistService>().IsReadOnly(playlist.Id)).ToArray();

    public bool IsBusy
    {
        get => _isBusy;
        protected set => SetProperty(ref _isBusy, value);
    }

    public virtual bool CanInteract => _operationCancellation is null && Context.IsInitialized && !_closing;
    public string Status => _statusKey is { } key ? _text[key] : "";
    protected bool IsNavigationActive => _isNavigationActive;
    internal void SetNavigationActive(bool active)
    {
        _isNavigationActive = active;
        if (!active || !_localizationDirty) return;
        _localizationDirty = false;
        RefreshLocalizedProperties();
    }
    internal bool HasActiveOperation => _operationCancellation is not null;
    internal event EventHandler? OperationCompleted;
    internal virtual object? CaptureNavigationState() => null;
    internal virtual void RestoreNavigationState(object? state) { }
    public RelayCommand CancelOperationCommand { get; }

    public virtual async ValueTask DisposeAsync()
    {
        _closing = true;
        Context.PropertyChanged -= OnContextChanged;
        _text.Changed -= OnLanguageChanged;
        await _lifetime.CancelAsync();
        await _operation;
        _lifetime.Dispose();
    }

    public virtual Task ActivateAsync()
    {
        return Task.CompletedTask;
    }

    protected Task ExecuteAsync(Func<CancellationToken, Task> action)
    {
        return RunOperationAsync(action);
    }

    public Task AddTrackAsync(Guid playlistId, Track track) => AddTracksToPlaylistAsync(playlistId, [track]);

    protected async Task<bool> AddTracksToPlaylistAsync(Guid playlistId, Track[] tracks)
    {
        if (!CanInteract || Context.Playlists.FirstOrDefault(p => p.Id == playlistId) is not { } target)
            return false;
        var insertAtStart = Context.Settings.Get<LibraryPreferences>().InsertAtStart;
        var succeeded = false;
        await ExecuteAsync(async ct =>
        {
            await _host.Settings.SaveAsync(ct);
            var playlists = _host.Get<PlaylistService>();
            var added = playlists.AddRange(target.Id, tracks, insertAtStart);
            try
            {
                await _host.Get<LibraryService>().SaveAsync(ct);
            }
            catch
            {
                foreach (var item in added) playlists.Remove(target.Id, item.Id);
                Context.RefreshLibrary();
                throw;
            }

            Context.RefreshLibrary();
            Notify(NotificationLevel.Success, LanguageManager.Keys.TracksAdded, added.Count, target.Name);
            succeeded = true;
        });
        return succeeded;
    }

    protected Task RunOperationAsync(Func<CancellationToken, Task> action, LinguaKey? status = null,
        bool showBusy = true, TimeSpan? busyDelay = null)
    {
        // Operation ownership controls reentrancy; IsBusy only controls visible progress.
        if (_operationCancellation is not null || _closing || !Context.IsInitialized) return Task.CompletedTask;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operationCancellation = cancellation;
        _operation = RunCoreAsync(action, status ?? LanguageManager.Keys.Working, cancellation, showBusy,
            busyDelay ?? TimeSpan.Zero);
        return _operation;
    }

    private async Task RunCoreAsync(Func<CancellationToken, Task> action, LinguaKey status,
        CancellationTokenSource cancellation,
        bool showBusy, TimeSpan busyDelay)
    {
        using var progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var progress = showBusy ? ShowBusyAsync(busyDelay, progressCancellation.Token) : Task.CompletedTask;
        RaiseInteractionProperties();
        if (showBusy) SetStatus(status);
        try
        {
            await action(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Notify(NotificationLevel.Error, LanguageManager.Keys.OperationFailed, exception.Message);
        }
        finally
        {
            progressCancellation.Cancel();
            await progress;
            IsBusy = false;
            if (showBusy) SetStatus(null);
            _operationCancellation = null;
            cancellation.Dispose();
            RaiseInteractionProperties();
            OperationCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ShowBusyAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            IsBusy = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    protected void SetStatus(LinguaKey? key)
    {
        _statusKey = key;
        OnPropertyChanged(nameof(Status));
    }

    protected void Notify(NotificationLevel level, LinguaKey key, params object?[] arguments)
    {
        if (!_closing) Context.Notifications.Show(_text.Format(key, arguments), level);
    }

    protected virtual void RaiseInteractionProperties()
    {
        OnPropertyChanged(nameof(CanInteract));
        CancelOperationCommand.NotifyCanExecuteChanged();
    }

    protected virtual void RefreshLocalizedProperties()
    {
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageDescription));
        OnPropertyChanged(nameof(PageSummary));
        OnPropertyChanged(nameof(Status));
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (_isNavigationActive) RefreshLocalizedProperties();
        else _localizationDirty = true;
    }

    private void OnContextChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_isNavigationActive) RaiseInteractionProperties();
    }
}
