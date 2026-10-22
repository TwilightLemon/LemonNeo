using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.ViewModels.Settings;

/// <summary>One editable row in the keyboard-shortcut settings section.</summary>
public sealed class ShortcutRowViewModel : ObservableObject
{
    private readonly ShortcutAction _action;
    private readonly ShortcutService _shortcuts;
    private readonly LocalizationService _text;
    private string _conflictText = "";
    private bool _hasConflict;
    private bool _isCapturing;

    public ShortcutRowViewModel(ShortcutService shortcuts, LocalizationService text, ShortcutAction action)
    {
        _shortcuts = shortcuts;
        _text = text;
        _action = action;
        CapturedCommand = new RelayCommand<KeyGesture?>(Capture);
        CancelCaptureCommand = new RelayCommand(CancelCapture);
        ResetCommand = new RelayCommand(() => _shortcuts.Reset(action.Id));
        ClearCommand = new RelayCommand(() => _shortcuts.SetGesture(action.Id, null));
    }

    public string Id => _action.Id;

    public string Name => _text[_action.NameKey];

    public string GestureText => _shortcuts.DescribeGesture(_shortcuts.GetGesture(_action.Id));

    public string CapturePrompt => _text[LanguageManager.Keys.ShortcutCapturePrompt];

    public bool IsDefault => !_shortcuts.IsOverridden(_action.Id);

    public bool CanReset => _shortcuts.IsOverridden(_action.Id);

    public bool IsCapturing
    {
        get => _isCapturing;
        set
        {
            if (!SetProperty(ref _isCapturing, value)) return;
            if (value)
            {
                // Starting a new recording discards the previous conflict notice.
                HasConflict = false;
                // Suspend window accelerators so the pressed chord reaches the capture control.
                _shortcuts.BeginCapture();
            }
            else
            {
                _shortcuts.EndCapture();
            }
        }
    }

    public bool HasConflict
    {
        get => _hasConflict;
        private set => SetProperty(ref _hasConflict, value);
    }

    public string ConflictText
    {
        get => _conflictText;
        private set => SetProperty(ref _conflictText, value);
    }

    public RelayCommand<KeyGesture?> CapturedCommand { get; }
    public RelayCommand CancelCaptureCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand ClearCommand { get; }

    private void Capture(KeyGesture? gesture)
    {
        IsCapturing = false;
        // A null gesture means the user pressed Delete/Backspace to disable the action.
        if (gesture is null)
        {
            HasConflict = false;
            _shortcuts.SetGesture(_action.Id, null);
            return;
        }

        if (_shortcuts.FindConflict(_action.Id, gesture) is { } conflict)
        {
            HasConflict = true;
            ConflictText = _text.Format(LanguageManager.Keys.ShortcutConflict, _text[conflict.NameKey]);
            return;
        }

        HasConflict = false;
        _shortcuts.SetGesture(_action.Id, gesture);
    }

    private void CancelCapture()
    {
        IsCapturing = false;
        HasConflict = false;
    }
}
