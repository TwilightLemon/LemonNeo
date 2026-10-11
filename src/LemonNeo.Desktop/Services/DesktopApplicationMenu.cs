using Avalonia.Controls;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.ViewModels.Playback;

namespace LemonNeo.Desktop.Services;

/// <summary>Windows tray and macOS Dock entry points for a hidden main window.</summary>
public sealed class DesktopApplicationMenu : IDisposable
{
    private readonly Avalonia.Application _application;
    private readonly NativeMenuItem _exit;
    private readonly NativeMenu _menu;
    private readonly NativeMenuItem _next;
    private readonly PlaybackViewModel _playback;
    private readonly NativeMenuItem _previous;
    private readonly NativeMenuItem _showWindow;
    private readonly LocalizationService _text;
    private readonly NativeMenuItem _togglePlayback;
    private readonly TrayIcon? _trayIcon;

    public DesktopApplicationMenu(Avalonia.Application application, DesktopLifetimeService lifetime,
        PlaybackViewModel playback, LocalizationService text, bool useTrayIcon)
    {
        _application = application;
        _playback = playback;
        _text = text;
        _showWindow = new NativeMenuItem { Command = new RelayCommand(lifetime.ShowMainWindow) };
        _previous = new NativeMenuItem { Command = playback.PreviousCommand };
        _togglePlayback = new NativeMenuItem { Command = playback.TogglePlaybackCommand };
        _next = new NativeMenuItem { Command = playback.NextCommand };
        _exit = new NativeMenuItem { Command = new AsyncRelayCommand(lifetime.ShutdownAsync) };
        _menu = new NativeMenu
        {
            _showWindow, new NativeMenuItemSeparator(), _previous, _togglePlayback, _next,
            new NativeMenuItemSeparator(), _exit
        };
        if (useTrayIcon)
        {
            using var icon = AssetLoader.Open(new Uri("avares://LemonNeo.Desktop/Resources/Icons/App.ico"));
            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(icon), Menu = _menu, IsVisible = true,
                Command = _showWindow.Command
            };
            TrayIcon.SetIcons(application, [_trayIcon]);
        }
        else
        {
            NativeDock.SetMenu(application, _menu);
        }

        text.Changed += OnChanged;
        playback.Changed += OnChanged;
        Refresh();
    }

    public void Dispose()
    {
        _text.Changed -= OnChanged;
        _playback.Changed -= OnChanged;
        if (_trayIcon is not null)
        {
            _trayIcon.Dispose();
            TrayIcon.SetIcons(_application, null);
        }
        else if (NativeDock.GetMenu(_application) == _menu)
        {
            NativeDock.SetMenu(_application, null);
        }
    }

    private void OnChanged(object? sender, EventArgs args)
    {
        Refresh();
    }

    private void Refresh()
    {
        _showWindow.Header = _text[LanguageManager.Keys.ShowMainWindow];
        _previous.Header = _text[LanguageManager.Keys.Previous];
        _togglePlayback.Header = _playback.PlayLabel;
        _next.Header = _text[LanguageManager.Keys.Next];
        _exit.Header = _text[LanguageManager.Keys.ExitApplication];
        _previous.IsEnabled = _playback.CanPrevious;
        _togglePlayback.IsEnabled = _playback.CanTogglePlayback;
        _next.IsEnabled = _playback.CanNext;
        if (_trayIcon is not null) _trayIcon.ToolTipText = _text[LanguageManager.Keys.AppName];
    }
}