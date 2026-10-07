using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.Controls.Shared;

public static class TrackSelectionBehavior
{
    public static void Attach(ListBox list)
    {
        list.AddHandler(InputElement.PointerReleasedEvent, (_, args) =>
        {
            if (list.DataContext is not TrackListViewModel { IsSelectionMode: true, CanInteract: true }) return;
            if (args.InitialPressMouseButton != MouseButton.Left) return;
            if (args.Source is not Control control || control.GetSelfAndVisualAncestors().Any(v => v is CheckBox)) return;
            if (control.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext is TrackRow row)
            {
                row.IsMarked = !row.IsMarked;
                args.Handled = true;
            }
        }, RoutingStrategies.Bubble);
        list.AddHandler(InputElement.KeyDownEvent, (_, args) =>
        {
            if (list.DataContext is not TrackListViewModel { CanInteract: true } vm) return;
            if (args.Key == Key.A && args.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control))
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
            else if (vm.IsSelectionMode && args.Key == Key.Space && vm.SelectedTrack is { } row && args.Source is not CheckBox)
            {
                row.IsMarked = !row.IsMarked;
                args.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }
}
