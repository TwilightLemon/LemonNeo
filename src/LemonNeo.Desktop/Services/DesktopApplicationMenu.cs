using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.ViewModels.Playback;

namespace LemonNeo.Desktop.Services;

/// <summary>Windows tray and macOS Dock/menu bar entry points for a hidden main window.</summary>
public sealed class DesktopApplicationMenu : IDisposable
{
    private readonly Avalonia.Application _application;
    private readonly List<(string CategoryId, NativeMenuItem Item)> _categoryItems = [];
    private readonly NativeMenuItem _exit;
    private readonly NativeMenu _menu;
    private readonly NativeMenu? _menuBar;
    private readonly NativeMenuItem _next;
    private readonly PlaybackViewModel _playback;
    private readonly NativeMenuItem _previous;
    private readonly ShortcutService? _shortcuts;
    private readonly List<(ShortcutAction Action, NativeMenuItem Item)> _shortcutItems = [];
    private readonly NativeMenuItem _showWindow;
    private readonly LocalizationService _text;
    private readonly NativeMenuItem _togglePlayback;
    private readonly TrayIcon? _trayIcon;
    private readonly Window? _window;

    public DesktopApplicationMenu(Avalonia.Application application, DesktopLifetimeService lifetime,
        PlaybackViewModel playback, LocalizationService text, ShortcutService shortcuts, Window window,
        bool useTrayIcon)
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
            // The Dock menu cannot render key equivalents; the main menu bar can.
            NativeDock.SetMenu(application, _menu);
            _window = window;
            _shortcuts = shortcuts;
            _menuBar = BuildMenuBar(shortcuts);
            NativeMenu.SetMenu(window, _menuBar);
            shortcuts.Changed += OnChanged;
            shortcuts.CaptureStateChanged += OnChanged;
        }

        text.Changed += OnChanged;
        playback.Changed += OnChanged;
        Refresh();
    }

    public void Dispose()
    {
        _text.Changed -= OnChanged;
        _playback.Changed -= OnChanged;
        if (_shortcuts is not null)
        {
            _shortcuts.Changed -= OnChanged;
            _shortcuts.CaptureStateChanged -= OnChanged;
        }
        if (_trayIcon is not null)
        {
            _trayIcon.Dispose();
            TrayIcon.SetIcons(_application, null);
        }
        else if (NativeDock.GetMenu(_application) == _menu)
        {
            NativeDock.SetMenu(_application, null);
        }

        if (_window is not null && NativeMenu.GetMenu(_window) == _menuBar) NativeMenu.SetMenu(_window, null);
    }

    private NativeMenu BuildMenuBar(ShortcutService shortcuts)
    {
        var menuBar = new NativeMenu();
        foreach (var categoryId in (string[])
                 [ShortcutCategories.Playback, ShortcutCategories.Navigation, ShortcutCategories.Application])
        {
            var categoryItem = new NativeMenuItem();
            var submenu = new NativeMenu();
            foreach (var action in shortcuts.Actions.Where(action => action.CategoryId == categoryId))
            {
                var item = new NativeMenuItem { Command = shortcuts.CommandFor(action.Id) };
                _shortcutItems.Add((action, item));
                submenu.Add(item);
            }

            categoryItem.Menu = submenu;
            _categoryItems.Add((categoryId, categoryItem));
            menuBar.Add(categoryItem);
        }

        return menuBar;
    }

    private void OnChanged(object? sender, EventArgs args)
    {
        Refresh();
    }

    /// <summary>
    /// AppKit matches menu key equivalents before dispatching keys to the window, so a bare key such as
    /// Space would swallow text input. Those shortcuts stay window-only and are not shown in the menu.
    /// </summary>
    private static KeyGesture? MenuGesture(KeyGesture? gesture)
    {
        return gesture is { KeyModifiers: not KeyModifiers.None } ? gesture : null;
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
        foreach (var (categoryId, item) in _categoryItems) item.Header = _text[ShortcutCategories.NameKey(categoryId)];
        if (_shortcuts is not null)
            foreach (var (action, item) in _shortcutItems)
            {
                item.Header = _shortcuts.DisplayName(action);
                // Clear accelerators while recording so AppKit routes the keys to the capture control.
                item.Gesture = _shortcuts.IsCapturing ? null : MenuGesture(_shortcuts.GetGesture(action.Id));
                item.IsEnabled = item.Command?.CanExecute(null) ?? false;
            }

        if (_trayIcon is not null) _trayIcon.ToolTipText = _text[LanguageManager.Keys.AppName];
    }
}
