using System.Globalization;
using System.Windows.Input;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using Irihi.Lingua;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.ViewModels.Shell;
using LemonNeo.Desktop.Views.Shell;

namespace LemonNeo.Desktop.Services;

/// <summary>Owns the desktop shortcut catalog and resolves user overrides into window bindings and menu items.</summary>
public sealed class ShortcutService : IDisposable
{
    private readonly Dictionary<string, ICommand> _commands = new();
    private readonly ISettingsScope _settings;
    private readonly LocalizationService _text;
    private readonly IDisposable _subscription;

    public ShortcutService(ISettingsScope settings, LocalizationService text,
        MainViewModel viewModel, MainWindow window)
    {
        _settings = settings;
        _text = text;
        Actions = CreateCatalog(viewModel, window);
        foreach (var action in Actions) _commands[action.Id] = action.Command();
        _subscription = settings.Subscribe<KeyboardShortcutPreferences>(_ => Changed?.Invoke(this, EventArgs.Empty));
    }

    public IReadOnlyList<ShortcutAction> Actions { get; }

    public event EventHandler? Changed;

    /// <summary>Raised when gesture recording starts or ends so bindings and menu accelerators can be suspended.</summary>
    public event EventHandler? CaptureStateChanged;

    /// <summary>True while the settings page is recording a gesture.</summary>
    public bool IsCapturing { get; private set; }

    public void BeginCapture()
    {
        if (IsCapturing) return;
        IsCapturing = true;
        CaptureStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void EndCapture()
    {
        if (!IsCapturing) return;
        IsCapturing = false;
        CaptureStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The effective gesture: override, default, or <c>null</c> when disabled.</summary>
    public KeyGesture? GetGesture(string id)
    {
        var bindings = _settings.Get<KeyboardShortcutPreferences>().Bindings;
        if (!bindings.TryGetValue(id, out var stored)) return DefaultGesture(id);
        // An empty value records an explicit "disabled" choice.
        return string.IsNullOrEmpty(stored) ? null : ParseOrNull(stored) ?? DefaultGesture(id);
    }

    public bool IsOverridden(string id) =>
        _settings.Get<KeyboardShortcutPreferences>().Bindings.ContainsKey(id);

    public void SetGesture(string id, KeyGesture? gesture)
    {
        // Recording the default again should not leave a redundant override behind.
        if (gesture is not null && DefaultGesture(id) is { } fallback && fallback.Equals(gesture))
        {
            if (IsOverridden(id)) Reset(id);
            return;
        }

        var value = gesture?.ToString() ?? "";
        _settings.Apply<KeyboardShortcutPreferences>(preferences =>
        {
            var bindings = new Dictionary<string, string>(preferences.Bindings) { [id] = value };
            return preferences with { Bindings = bindings };
        });
    }

    public void Reset(string id) => RemoveOverrides(id);

    public void ResetAll() => RemoveOverrides(null);

    /// <summary>Returns the action already bound to <paramref name="gesture"/>, if any.</summary>
    public ShortcutAction? FindConflict(string id, KeyGesture gesture)
    {
        foreach (var action in Actions)
        {
            if (action.Id == id) continue;
            if (GetGesture(action.Id) is { } current && current.Equals(gesture)) return action;
        }

        return null;
    }

    public IReadOnlyList<KeyBinding> BuildKeyBindings()
    {
        var result = new List<KeyBinding>();
        foreach (var action in Actions)
        {
            if (GetGesture(action.Id) is not { } gesture) continue;
            result.Add(new KeyBinding { Gesture = gesture, Command = CommandFor(action.Id) });
        }

        return result;
    }

    public ICommand CommandFor(string id) => _commands[id];

    public string DisplayName(ShortcutAction action) => _text[action.NameKey];

    public string DescribeGesture(KeyGesture? gesture) =>
        gesture is null
            ? _text[LanguageManager.Keys.ShortcutDisabled]
            : gesture.ToString("p", CultureInfo.CurrentCulture);

    public void Dispose() => _subscription.Dispose();

    private KeyGesture? DefaultGesture(string id) =>
        Actions.FirstOrDefault(action => action.Id == id)?.DefaultGesture;

    private void RemoveOverrides(string? id)
    {
        _settings.Apply<KeyboardShortcutPreferences>(preferences =>
        {
            var bindings = new Dictionary<string, string>(preferences.Bindings);
            if (id is null) bindings.Clear();
            else bindings.Remove(id);
            return preferences with { Bindings = bindings };
        });
    }

    private static KeyGesture? ParseOrNull(string value)
    {
        try
        {
            return KeyGesture.Parse(value);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static ShortcutAction[] CreateCatalog(MainViewModel viewModel, MainWindow window)
    {
        var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        return
        [
            new ShortcutAction("playback.toggle", LanguageManager.Keys.ShortcutTogglePlayback, ShortcutCategories.Playback,
                new KeyGesture(Key.Space, modifier), () => viewModel.Playback.TogglePlaybackCommand),
            new ShortcutAction("playback.previous", LanguageManager.Keys.ShortcutPrevious, ShortcutCategories.Playback,
                new KeyGesture(Key.Left, modifier), () => viewModel.Playback.PreviousCommand),
            new ShortcutAction("playback.next", LanguageManager.Keys.ShortcutNext, ShortcutCategories.Playback,
                new KeyGesture(Key.Right, modifier), () => viewModel.Playback.NextCommand),
            new ShortcutAction("playback.stop", LanguageManager.Keys.ShortcutStop, ShortcutCategories.Playback,
                new KeyGesture(Key.S, modifier | KeyModifiers.Alt), () => viewModel.Playback.StopCommand),
            new ShortcutAction("playback.cycleMode", LanguageManager.Keys.ShortcutCycleMode, ShortcutCategories.Playback,
                new KeyGesture(Key.M, modifier), () => viewModel.Playback.CycleModeCommand),
            new ShortcutAction("navigation.back", LanguageManager.Keys.ShortcutBack, ShortcutCategories.Navigation,
                new KeyGesture(Key.Left, KeyModifiers.Alt), () => viewModel.Navigation.BackCommand),
            new ShortcutAction("navigation.forward", LanguageManager.Keys.ShortcutForward, ShortcutCategories.Navigation,
                new KeyGesture(Key.Right, KeyModifiers.Alt), () => viewModel.Navigation.ForwardCommand),
            new ShortcutAction("app.focusSearch", LanguageManager.Keys.ShortcutFocusSearch, ShortcutCategories.Application,
                new KeyGesture(Key.F, modifier), () => new RelayCommand(window.FocusSearch)),
            new ShortcutAction("app.toggleQueue", LanguageManager.Keys.ShortcutToggleQueue, ShortcutCategories.Application,
                new KeyGesture(Key.J, modifier), () => viewModel.ToggleQueueCommand),
            new ShortcutAction("app.createPlaylist", LanguageManager.Keys.ShortcutCreatePlaylist, ShortcutCategories.Application,
                new KeyGesture(Key.O, modifier), () => new AsyncRelayCommand(() => window.ShowCreatePlaylistAsync(true))),
            new ShortcutAction("app.toggleLyrics", LanguageManager.Keys.ShortcutToggleLyrics, ShortcutCategories.Application,
                new KeyGesture(Key.E, modifier), () => viewModel.ToggleLyricsExpandedCommand),
            new ShortcutAction("app.toggleDesktopLyrics", LanguageManager.Keys.ShortcutToggleDesktopLyrics,
                ShortcutCategories.Application, new KeyGesture(Key.L, modifier),
                () => new RelayCommand(window.ToggleDesktopLyrics))
        ];
    }
}

public sealed record ShortcutAction(
    string Id,
    LinguaKey NameKey,
    string CategoryId,
    KeyGesture DefaultGesture,
    Func<ICommand> Command);

public static class ShortcutCategories
{
    public const string Playback = "playback";
    public const string Navigation = "navigation";
    public const string Application = "application";

    public static LinguaKey NameKey(string categoryId) => categoryId switch
    {
        Playback => LanguageManager.Keys.MenuPlayback,
        Navigation => LanguageManager.Keys.MenuNavigation,
        _ => LanguageManager.Keys.MenuApplication
    };
}
