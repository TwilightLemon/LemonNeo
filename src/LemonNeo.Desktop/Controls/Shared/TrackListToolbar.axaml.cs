using Avalonia;
using Avalonia.Controls;

namespace LemonNeo.Desktop.Controls.Shared;

public sealed partial class TrackListToolbar : UserControl
{
    public static readonly StyledProperty<Control?> LeadingContentProperty =
        AvaloniaProperty.Register<TrackListToolbar, Control?>(nameof(LeadingContent));

    public static readonly StyledProperty<Control?> SelectionActionsProperty =
        AvaloniaProperty.Register<TrackListToolbar, Control?>(nameof(SelectionActions));
    public static readonly StyledProperty<bool> ShowTrackActionsProperty =
        AvaloniaProperty.Register<TrackListToolbar, bool>(nameof(ShowTrackActions), true);

    public TrackListToolbar() => InitializeComponent();

    public Control? LeadingContent
    {
        get => GetValue(LeadingContentProperty);
        set => SetValue(LeadingContentProperty, value);
    }

    public Control? SelectionActions
    {
        get => GetValue(SelectionActionsProperty);
        set => SetValue(SelectionActionsProperty, value);
    }

    public bool ShowTrackActions
    {
        get => GetValue(ShowTrackActionsProperty);
        set => SetValue(ShowTrackActionsProperty, value);
    }
}
