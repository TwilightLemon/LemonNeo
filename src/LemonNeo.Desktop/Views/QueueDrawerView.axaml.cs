using Avalonia.Controls;
using Avalonia.Input;
using LemonNeo.Desktop.ViewModels;

namespace LemonNeo.Desktop.Views;

public partial class QueueDrawerView : UserControl
{
    public QueueDrawerView() => InitializeComponent();
    private async void OnTrackTapped(object? sender, TappedEventArgs args)
    {
        if (DataContext is PlaybackQueueViewModel vm && args.Source is Control { DataContext: QueueTrackViewModel row })
            await vm.PlayTrackAsync(row);
    }
    private async void OnTrackKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter && DataContext is PlaybackQueueViewModel { SelectedTrack: { } row } vm)
        {
            args.Handled = true;
            await vm.PlayTrackAsync(row);
        }
    }
}
