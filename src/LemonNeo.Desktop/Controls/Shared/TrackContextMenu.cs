using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Core.Library;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Shared;
using LemonNeo.Desktop.Views.Shell;

namespace LemonNeo.Desktop.Controls.Shared;

internal static class TrackContextMenu
{
    public static (ListBoxItem Container, T Row)? GetTarget<T>(ListBox list, ContextRequestedEventArgs args)
        where T : class
    {
        var container = (args.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (container is null && !args.TryGetPosition(null, out _) && list.SelectedItem is { } selected)
            container = list.ContainerFromItem(selected) as ListBoxItem;
        if (container is null || list.ItemFromContainer(container) is not T row) return null;

        list.SetCurrentValue(ListBox.SelectedItemProperty, row);
        return (container, row);
    }

    public static void Open(ListBoxItem container, ContextRequestedEventArgs args, TrackRow row,
        IEnumerable<Playlist> playlists, Func<Guid, Task> add, Func<Task> inspect, Func<Task>? remove)
    {
        var destinations = playlists.Select(playlist => new MenuItem
        {
            Header = playlist.Name,
            Command = new AsyncRelayCommand(() => add(playlist.Id))
        }).ToArray();
        var menu = new ContextMenu
        {
            Placement = args.TryGetPosition(null, out _) ? PlacementMode.Pointer : PlacementMode.Bottom
        };
        menu.Items.Add(new MenuItem
        {
            Header = row.Text[LanguageManager.Keys.AddToPlaylist],
            IsEnabled = destinations.Length > 0,
            ItemsSource = destinations
        });
        menu.Items.Add(new MenuItem
        {
            Header = row.Text[LanguageManager.Keys.TrackDetails],
            Command = new AsyncRelayCommand(inspect)
        });
        if (TopLevel.GetTopLevel(container) is MainWindow window)
            menu.Items.Add(new MenuItem
            {
                Header = row.Text[LanguageManager.Keys.ExportTracks],
                Command = new AsyncRelayCommand(() => window.ExportTracksAsync([row.Track]))
            });
        if (remove is not null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem
            {
                Header = row.Text[LanguageManager.Keys.RemoveTrack],
                Command = new AsyncRelayCommand(remove)
            });
        }

        // Attach to the row so the menu closes if its virtualized container is detached.
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(container.ContextMenu, menu)) container.ContextMenu = null;
        };
        container.ContextMenu = menu;
        menu.Open(container);
        args.Handled = true;
    }
}
