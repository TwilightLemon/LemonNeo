using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LemonNeo.Desktop.Views.Library;

public partial class MoveTrackDialog : Window
{
    public MoveTrackDialog() : this(1, 1)
    {
    }

    public MoveTrackDialog(int position, int count)
    {
        InitializeComponent();
        DestinationInput.Maximum = Math.Max(1, count);
        DestinationInput.Value = position;
        DestinationInput.ValueChanged += (_, _) => MoveButton.IsEnabled = IsValidDestination();
        MoveButton.IsEnabled = IsValidDestination();
    }

    private void OnCancel(object? sender, RoutedEventArgs args)
    {
        Close(null);
    }

    private void OnMove(object? sender, RoutedEventArgs args)
    {
        if (IsValidDestination() && DestinationInput.Value is { } value) Close((int)value);
    }

    private bool IsValidDestination()
    {
        return DestinationInput.Value is { } value && value >= 1 && value <= DestinationInput.Maximum &&
               value == decimal.Truncate(value);
    }
}