using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace LemonNeo.Desktop.Controls.Shared;

/// <summary>Displays a gesture and records a new one while capturing.</summary>
public partial class ShortcutCaptureBox : UserControl
{
    public static readonly StyledProperty<string> GestureTextProperty =
        AvaloniaProperty.Register<ShortcutCaptureBox, string>(nameof(GestureText), "");

    public static readonly StyledProperty<string> CapturePromptProperty =
        AvaloniaProperty.Register<ShortcutCaptureBox, string>(nameof(CapturePrompt), "");

    public static readonly StyledProperty<bool> IsCapturingProperty =
        AvaloniaProperty.Register<ShortcutCaptureBox, bool>(nameof(IsCapturing),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<ICommand?> CapturedCommandProperty =
        AvaloniaProperty.Register<ShortcutCaptureBox, ICommand?>(nameof(CapturedCommand));

    public static readonly StyledProperty<ICommand?> CancelledCommandProperty =
        AvaloniaProperty.Register<ShortcutCaptureBox, ICommand?>(nameof(CancelledCommand));

    private TopLevel? _captureTarget;

    public ShortcutCaptureBox()
    {
        InitializeComponent();
        CaptureSurface.LostFocus += (_, _) => IsCapturing = false;
        UpdateDisplay();
    }

    public string GestureText
    {
        get => GetValue(GestureTextProperty);
        set => SetValue(GestureTextProperty, value);
    }

    public string CapturePrompt
    {
        get => GetValue(CapturePromptProperty);
        set => SetValue(CapturePromptProperty, value);
    }

    public bool IsCapturing
    {
        get => GetValue(IsCapturingProperty);
        set => SetValue(IsCapturingProperty, value);
    }

    public ICommand? CapturedCommand
    {
        get => GetValue(CapturedCommandProperty);
        set => SetValue(CapturedCommandProperty, value);
    }

    public ICommand? CancelledCommand
    {
        get => GetValue(CancelledCommandProperty);
        set => SetValue(CancelledCommandProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCapturingProperty) UpdateCaptureHook();
        if (change.Property == GestureTextProperty || change.Property == CapturePromptProperty ||
            change.Property == IsCapturingProperty)
            UpdateDisplay();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        if (IsCapturing) IsCapturing = false;
        var target = _captureTarget;
        _captureTarget = null;
        if (target is not null)
            // The row can be rebuilt from inside a key handler; detach after the event has been routed.
            Dispatcher.UIThread.Post(() =>
            {
                target.RemoveHandler(KeyDownEvent, OnCaptureKeyDown);
                target.RemoveHandler(PointerPressedEvent, OnCapturePointerPressed);
            }, DispatcherPriority.Background);
        base.OnDetachedFromVisualTree(args);
    }

    private void UpdateCaptureHook()
    {
        if (!IsCapturing) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || ReferenceEquals(topLevel, _captureTarget)) return;
        _captureTarget = topLevel;
        // Capture at the window level so window shortcuts and focused controls cannot take the chord first.
        topLevel.AddHandler(KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel.AddHandler(PointerPressedEvent, OnCapturePointerPressed, RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    private void OnCapturePointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!IsCapturing) return;
        var point = args.GetPosition(this);
        if (point.X >= 0 && point.Y >= 0 && point.X <= Bounds.Width && point.Y <= Bounds.Height) return;
        // Let the click reach its target; only stop recording.
        IsCapturing = false;
    }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs args)
    {
        if (!IsCapturing) return;
        args.Handled = true;
        switch (args.Key)
        {
            case Key.Escape:
                IsCapturing = false;
                CancelledCommand?.Execute(null);
                break;
            case Key.Back or Key.Delete:
                // An empty gesture disables the action.
                IsCapturing = false;
                CapturedCommand?.Execute(null);
                break;
            default:
                // Wait for the non-modifier key that completes the chord.
                if (IsModifierKey(args.Key)) return;
                var gesture = new KeyGesture(args.Key, args.KeyModifiers);
                IsCapturing = false;
                CapturedCommand?.Execute(gesture);
                break;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        IsCapturing = true;
        CaptureSurface.Focus();
        args.Handled = true;
    }

    private void UpdateDisplay()
    {
        if (DisplayTextBlock is null) return;
        DisplayTextBlock.Text = IsCapturing ? CapturePrompt : GestureText;
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift
        or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
}
