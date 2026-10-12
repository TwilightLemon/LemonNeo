using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Irihi.Lingua;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.ViewModels.Shared;

public abstract class PageViewModel : ObservableObject, IAsyncDisposable
{
    protected readonly DesktopContext Context;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _closing;
    private object?[] _errorArguments = [];
    private string? _errorDetails;
    private LinguaKey? _errorKey;
    private bool _isBusy;
    private bool _isNavigationActive = true;
    private bool _localizationDirty;
    private Task _operation = Task.CompletedTask;
    private CancellationTokenSource? _operationCancellation;
    private object?[] _statusArguments = [];
    private LinguaKey? _statusKey;

    protected PageViewModel(DesktopContext context, string route)
    {
        Context = context;
        Route = route;
        CancelOperationCommand =
            new RelayCommand(() => _operationCancellation?.Cancel(), () => _operationCancellation is not null);
        DismissErrorCommand = new RelayCommand(() => SetError(null));
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

    public bool IsBusy
    {
        get => _isBusy;
        protected set => SetProperty(ref _isBusy, value);
    }

    public virtual bool CanInteract => _operationCancellation is null && Context.IsInitialized && !_closing;
    public bool HasStatus => _statusKey is not null;
    public string Status => _statusKey is { } key ? _text.Format(key, _statusArguments) : "";

    public string? Error => _errorKey is null
        ? null
        : _text.Format(_errorKey, _errorArguments) +
          (_errorDetails is null ? "" : Environment.NewLine + _errorDetails);

    public bool HasError => _errorKey is not null;
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
    public RelayCommand DismissErrorCommand { get; }

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
        SetError(null);
        if (showBusy) SetStatus(status);
        try
        {
            await action(cancellation.Token);
            if (showBusy && _statusKey == status) SetStatus(null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (showBusy) SetStatus(LanguageManager.Keys.Cancelled);
        }
        catch (Exception exception)
        {
            SetError(LanguageManager.Keys.OperationFailed, [exception.Message]);
            if (showBusy) SetStatus(null);
        }
        finally
        {
            progressCancellation.Cancel();
            await progress;
            IsBusy = false;
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

    protected void SetStatus(LinguaKey? key, params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(HasStatus));
    }

    protected void SetError(LinguaKey? key, object?[]? arguments = null, string? details = null)
    {
        _errorKey = key;
        _errorArguments = arguments ?? [];
        _errorDetails = details;
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
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
        OnPropertyChanged(nameof(Error));
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
