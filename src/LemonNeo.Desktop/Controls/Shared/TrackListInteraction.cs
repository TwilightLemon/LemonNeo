using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.Controls.Shared;

internal static class TrackListInteraction
{
    public static void Attach(ListBox list, Func<TrackRow, Func<Task>?>? getRemove = null)
    {
        list.ContextRequested += (_, args) =>
        {
            if (list.DataContext is not TrackListViewModel { CanInteract: true } vm ||
                TrackContextMenu.GetTarget<TrackRow>(list, args) is not { } target) return;

            TrackContextMenu.Open(target.Container, args, target.Row, vm.EditablePlaylists,
                id => vm.AddTrackAsync(id, target.Row.Track), () => vm.InspectTrackAsync(target.Row),
                getRemove?.Invoke(target.Row));
        };
        list.DoubleTapped += async (_, args) =>
        {
            if (args.Source is Visual source && source.GetSelfAndVisualAncestors().Any(visual => visual is Button))
                return;
            if (list.DataContext is TrackListViewModel { CanInteract: true, IsSelectionMode: false, SelectedTrack: { } row } vm)
                await vm.PlayAsync(row);
        };
        list.KeyDown += async (_, args) =>
        {
            if (args.Handled || args.Key != Key.Enter || args.Source is Button ||
                list.DataContext is not TrackListViewModel { CanInteract: true, IsSelectionMode: false, SelectedTrack: { } row } vm)
                return;
            args.Handled = true;
            await vm.PlayAsync(row);
        };

        list.AddHandler(InputElement.PointerReleasedEvent, (_, args) =>
        {
            if (list.DataContext is not TrackListViewModel { IsSelectionMode: true, CanInteract: true }) return;
            if (args.InitialPressMouseButton != MouseButton.Left) return;
            if (args.Source is not Control control ||
                control.GetSelfAndVisualAncestors().Any(v => v is CheckBox)) return;
            if (control.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext is TrackRow row)
            {
                row.IsMarked = !row.IsMarked;
                args.Handled = true;
            }
        }, RoutingStrategies.Bubble);
        list.AddHandler(InputElement.KeyDownEvent, (_, args) =>
        {
            if (list.DataContext is not TrackListViewModel { CanInteract: true } vm) return;
            if (args.Key == Key.A &&
                args.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control))
            {
                vm.IsSelectionMode = true;
                vm.SelectAllCommand.Execute(null);
                args.Handled = true;
            }
            else if (vm.IsSelectionMode && args.Key == Key.Escape)
            {
                vm.IsSelectionMode = false;
                args.Handled = true;
            }
            else if (vm.IsSelectionMode && args.Key == Key.Space && vm.SelectedTrack is { } row &&
                     args.Source is not CheckBox)
            {
                row.IsMarked = !row.IsMarked;
                args.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }
}
