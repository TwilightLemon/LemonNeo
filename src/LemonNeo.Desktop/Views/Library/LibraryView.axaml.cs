using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Desktop.Controls.Shared;
using LemonNeo.Desktop.ViewModels.Library;
using LemonNeo.Desktop.ViewModels.Shared;
using LemonNeo.Desktop.Views.Shell;

namespace LemonNeo.Desktop.Views.Library;

public partial class LibraryView : UserControl
{
    private readonly DispatcherTimer _syncTimeTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    public LibraryView()
    {
        InitializeComponent();
        _syncTimeTimer.Tick += (_, _) => (DataContext as LibraryViewModel)?.RefreshSyncTime();
        Loaded += (_, _) =>
        {
            if (DataContext is not LibraryViewModel { HasSelectedPlaylist: true } vm) return;
            vm.RefreshSyncTime();
            _syncTimeTimer.Start();
        };
        Unloaded += (_, _) => _syncTimeTimer.Stop();
        // Composite the faded list before the floating button samples its backdrop.
        //PageBottomFade.Attach(TrackList);
        CollapsibleListHeader.Attach(LibraryHeader, CompactTitle, LibraryTools, LibraryEmptyState, TrackList);
        _ = new ListReorderBehavior(TrackList, () => DataContext is LibraryViewModel { CanReorderTracks: true },
            (source, target, after) => DataContext is LibraryViewModel vm && source is TrackRow row &&
                                       target is TrackRow destination
                ? vm.ReorderTracksAsync(row, destination, after)
                : Task.CompletedTask);
        TrackListInteraction.Attach(TrackList, row =>
            DataContext is LibraryViewModel vm && vm.CanRemoveTrack(row) ? () => vm.RemoveTrackAsync(row) : null);
    }

    private void OnLocatePlaying(object? sender, RoutedEventArgs args)
    {
        if (DataContext is LibraryViewModel vm && vm.RevealPlayingTrack() is { } row)
            Dispatcher.UIThread.Post(() =>
            {
                TrackList.ScrollIntoView(row);
                TrackList.Focus();
            }, DispatcherPriority.Loaded);
    }

    private async void OnEditPlaylist(object? sender, RoutedEventArgs args)
    {
        if (this.GetVisualAncestors().OfType<MainWindow>().FirstOrDefault() is { } window)
            await window.EditPlaylistAsync();
    }

    private async void OnDeletePlaylist(object? sender, RoutedEventArgs args)
    {
        if (this.GetVisualAncestors().OfType<MainWindow>().FirstOrDefault() is { } window)
            await window.DeletePlaylistAsync();
    }

    private async void OnMoveTrack(object? sender, RoutedEventArgs args)
    {
        if (this.GetVisualAncestors().OfType<MainWindow>().FirstOrDefault() is { } window)
            await window.MoveTrackAsync();
    }
}
