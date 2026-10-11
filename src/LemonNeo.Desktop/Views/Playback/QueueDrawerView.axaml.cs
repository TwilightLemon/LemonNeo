using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LemonNeo.Desktop.ViewModels.Playback;

namespace LemonNeo.Desktop.Views.Playback;

public partial class QueueDrawerView : UserControl
{
    public QueueDrawerView()
    {
        InitializeComponent();
    }

    private void OnLocatePlaying(object? sender, RoutedEventArgs args)
    {
        if (DataContext is PlaybackQueueViewModel vm && vm.RevealPlayingTrack() is { } row)
            Dispatcher.UIThread.Post(() =>
            {
                QueueTracks.ScrollIntoView(row);
                QueueTracks.Focus();
            }, DispatcherPriority.Loaded);
    }

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