using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.Infrastructure;

namespace LemonNeo.Desktop.ViewModels;

public abstract class PageViewModel : ObservableObject, IAsyncDisposable
{
    protected readonly DesktopContext Context;
    protected DesktopRuntime _runtime => Context.Runtime;
    protected LocalizationService _text => Context.Text;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operationCancellation;
    private Task _operation = Task.CompletedTask;
    private bool _isBusy;
    private bool _closing;
    private string? _errorKey;
    private object?[] _errorArguments = [];
    private string? _errorDetails;
    private string _statusKey = "Ready";
    private object?[] _statusArguments = [];

    protected PageViewModel(DesktopContext context, string route)
    {
        Context = context;
        Route = route;
        CancelOperationCommand = new(() => _operationCancellation?.Cancel(), () => IsBusy);
        DismissErrorCommand = new(() => SetError(null));
        Context.PropertyChanged += OnContextChanged;
        _text.Changed += OnLanguageChanged;
    }

    public string Route { get; }
    public virtual string PageTitle => _text[Route];
    public virtual string PageDescription => _text[$"{Route}Description"];
    public virtual string? PageSummary => null;
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public bool CanInteract => !IsBusy && Context.IsInitialized && !_closing;
    public string Status => _text.Format(_statusKey, _statusArguments);
    public string? Error => _errorKey is null ? null : _text.Format(_errorKey, _errorArguments) +
        (_errorDetails is null ? "" : Environment.NewLine + _errorDetails);
    public bool HasError => _errorKey is not null;
    public RelayCommand CancelOperationCommand { get; }
    public RelayCommand DismissErrorCommand { get; }
    public virtual Task ActivateAsync() => Task.CompletedTask;

    protected Task ExecuteAsync(Func<CancellationToken, Task> action) => RunOperationAsync(action);
    protected Task RunOperationAsync(Func<CancellationToken, Task> action, string status = "Working", bool allowUninitialized = false)
    {
        if (IsBusy || _closing || !Context.IsInitialized && !allowUninitialized) return Task.CompletedTask;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operationCancellation = cancellation;
        _operation = RunCoreAsync(action, status, cancellation);
        return _operation;
    }

    private async Task RunCoreAsync(Func<CancellationToken, Task> action, string status, CancellationTokenSource cancellation)
    {
        IsBusy = true;
        RaiseInteractionProperties();
        SetError(null);
        SetStatus(status);
        try
        {
            await action(cancellation.Token);
            if (_statusKey == status) SetStatus("Ready");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { SetStatus("Cancelled"); }
        catch (Exception exception)
        {
            SetError(Context.IsInitialized ? "OperationFailed" : "StartupFailed", [exception.Message]);
            SetStatus("Ready");
        }
        finally
        {
            IsBusy = false;
            _operationCancellation = null;
            cancellation.Dispose();
            RaiseInteractionProperties();
        }
    }

    protected void SetStatus(string key, params object?[] arguments)
    {
        _statusKey = key; _statusArguments = arguments; OnPropertyChanged(nameof(Status));
    }
    protected void SetError(string? key, object?[]? arguments = null, string? details = null)
    {
        _errorKey = key; _errorArguments = arguments ?? []; _errorDetails = details;
        OnPropertyChanged(nameof(Error)); OnPropertyChanged(nameof(HasError));
    }
    protected virtual void RaiseInteractionProperties()
    {
        OnPropertyChanged(nameof(CanInteract));
        CancelOperationCommand.NotifyCanExecuteChanged();
    }
    protected virtual void RefreshLocalizedProperties()
    {
        OnPropertyChanged(nameof(PageTitle)); OnPropertyChanged(nameof(PageDescription)); OnPropertyChanged(nameof(PageSummary));
        OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(Error));
    }
    private void OnLanguageChanged(object? sender, EventArgs args) => RefreshLocalizedProperties();
    private void OnContextChanged(object? sender, PropertyChangedEventArgs args) => RaiseInteractionProperties();

    public virtual async ValueTask DisposeAsync()
    {
        _closing = true;
        Context.PropertyChanged -= OnContextChanged;
        _text.Changed -= OnLanguageChanged;
        await _lifetime.CancelAsync();
        await _operation;
        _lifetime.Dispose();
    }
}
