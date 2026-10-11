using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace LemonNeo.Desktop.Services;

/// <summary>Keeps window visibility separate from initialization and application shutdown.</summary>
public sealed class DesktopLifetimeService : IDisposable
{
    private readonly IActivatableLifetime? _activationLifetime;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly Action _exitApplication;
    private readonly Func<Task> _initialize;
    private readonly bool _keepRunningOnClose;
    private readonly Action? _prepareForBackground;
    private readonly Func<Task> _shutdown;
    private readonly Window _window;
    private bool _shutdownComplete;
    private bool _shutdownStarted;
    private Task? _shutdownTask;

    public DesktopLifetimeService(IClassicDesktopStyleApplicationLifetime desktop, Window window,
        bool keepRunningOnClose, Func<Task> initialize, Func<Task> shutdown,
        IActivatableLifetime? activationLifetime = null, Action? prepareForBackground = null,
        Action? exitApplication = null)
    {
        _desktop = desktop;
        _window = window;
        _keepRunningOnClose = keepRunningOnClose;
        _initialize = initialize;
        _shutdown = shutdown;
        _exitApplication = exitApplication ?? (() => desktop.Shutdown());
        _activationLifetime = activationLifetime;
        _prepareForBackground = prepareForBackground;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        window.Opened += OnOpened;
        window.Closing += OnClosing;
        desktop.ShutdownRequested += OnShutdownRequested;
        if (activationLifetime is not null) activationLifetime.Activated += OnActivated;
    }

    public void Dispose()
    {
        _window.Opened -= OnOpened;
        _window.Closing -= OnClosing;
        _desktop.ShutdownRequested -= OnShutdownRequested;
        if (_activationLifetime is not null) _activationLifetime.Activated -= OnActivated;
    }

    private async void OnOpened(object? sender, EventArgs args)
    {
        // Show() raises Opened again after Hide(); startup must run only once.
        _window.Opened -= OnOpened;
        await _initialize();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_shutdownComplete) return;
        args.Cancel = true;
        if (!_shutdownStarted && _keepRunningOnClose &&
            args.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined)
        {
            _prepareForBackground?.Invoke();
            _window.Hide();
            return;
        }

        _ = ShutdownAsync();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs args)
    {
        if (_shutdownComplete) return;
        args.Cancel = true;
        _ = ShutdownAsync();
    }

    private void OnActivated(object? sender, ActivatedEventArgs args)
    {
        if (args.Kind == ActivationKind.Reopen)
            Dispatcher.UIThread.Post(ShowMainWindow);
    }

    public void ShowMainWindow()
    {
        if (_shutdownStarted) return;
        _activationLifetime?.TryLeaveBackground();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Show();
        _window.Activate();
    }

    public Task ShutdownAsync()
    {
        return _shutdownTask ??= ShutdownCoreAsync();
    }

    private async Task ShutdownCoreAsync()
    {
        _shutdownStarted = true;
        _window.IsEnabled = false;
        // Leave the native close/shutdown callback before closing any windows.
        await Task.Yield();
        try
        {
            await _shutdown();
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Desktop shutdown failed: {exception}");
        }
        finally
        {
            _shutdownComplete = true;
            Dispose();
            _exitApplication();
        }
    }
}