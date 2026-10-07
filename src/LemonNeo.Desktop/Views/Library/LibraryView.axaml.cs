using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LemonNeo.Desktop.ViewModels.Library;
using LemonNeo.Desktop.Views.Shell;
using LemonNeo.Desktop.Controls.Shared;
using LemonNeo.Desktop.ViewModels.Shared;
using Avalonia.Threading;

namespace LemonNeo.Desktop.Views.Library;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        _ = new ListReorderBehavior(TrackList, () => DataContext is LibraryViewModel { CanReorderTracks: true },
            (source, target, after) => DataContext is LibraryViewModel vm && source is TrackRow row && target is TrackRow destination
                ? vm.ReorderTracksAsync(row, destination, after) : Task.CompletedTask);
        TrackSelectionBehavior.Attach(TrackList);
    }
    private void OnLocatePlaying(object? sender, RoutedEventArgs args)
    {
        if (DataContext is LibraryViewModel vm && vm.RevealPlayingTrack() is { } row)
            Dispatcher.UIThread.Post(() => { TrackList.ScrollIntoView(row); TrackList.Focus(); }, DispatcherPriority.Loaded);
    }
    private async void OnEditPlaylist(object? sender, RoutedEventArgs args)
    {
        if (this.GetVisualAncestors().OfType<MainWindow>().FirstOrDefault() is { } window) await window.EditPlaylistAsync();
    }
    private async void OnDeletePlaylist(object? sender, RoutedEventArgs args)
    {
        if (this.GetVisualAncestors().OfType<MainWindow>().FirstOrDefault() is { } window) await window.DeletePlaylistAsync();
    }
    private async void OnMoveTrack(object? sender, RoutedEventArgs args)
    {
        if (this.GetVisualAncestors().OfType<MainWindow>().FirstOrDefault() is { } window) await window.MoveTrackAsync();
    }
    private async void OnTrackDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (DataContext is LibraryViewModel { IsSelectionMode: false, SelectedTrack: { } track } vm) await vm.PlayAsync(track);
    }
    private async void OnTrackKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter && DataContext is LibraryViewModel { IsSelectionMode: false, SelectedTrack: { } track } vm)
        {
            args.Handled = true;
            await vm.PlayAsync(track);
        }
    }
}
