using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LemonNeo.Desktop.ViewModels.Shared;
using LemonNeo.Desktop.Views.Shell;

namespace LemonNeo.Desktop.Controls.Shared;

public sealed partial class TrackListToolbar : UserControl
{
    public static readonly StyledProperty<Control?> LeadingContentProperty =
        AvaloniaProperty.Register<TrackListToolbar, Control?>(nameof(LeadingContent));

    public static readonly StyledProperty<Control?> SelectionActionsProperty =
        AvaloniaProperty.Register<TrackListToolbar, Control?>(nameof(SelectionActions));
    public static readonly StyledProperty<Control?> SelectionHintProperty =
        AvaloniaProperty.Register<TrackListToolbar, Control?>(nameof(SelectionHint));
    public static readonly StyledProperty<bool> ShowTrackActionsProperty =
        AvaloniaProperty.Register<TrackListToolbar, bool>(nameof(ShowTrackActions), true);

    public TrackListToolbar() => InitializeComponent();

    private async void OnExportTracks(object? sender, RoutedEventArgs args)
    {
        if (DataContext is TrackListViewModel { HasActionTracks: true } vm && TopLevel.GetTopLevel(this) is MainWindow window)
            await window.ExportTracksAsync(vm.ActionTracks.Select(row => row.Track).ToArray());
    }

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

    public Control? SelectionHint
    {
        get => GetValue(SelectionHintProperty);
        set => SetValue(SelectionHintProperty, value);
    }
}
