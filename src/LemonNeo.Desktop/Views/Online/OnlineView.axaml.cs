using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LemonNeo.Desktop.ViewModels.Online;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.Views.Online;

public partial class OnlineView : UserControl
{
    private string? _serviceBeforePress;

    public OnlineView()
    {
        InitializeComponent();
        ServiceTabs.AddHandler(PointerPressedEvent, (_, _) =>
            _serviceBeforePress = (DataContext as OnlineViewModel)?.SelectedService?.Id, RoutingStrategies.Tunnel);
        ServiceTabs.AddHandler(TappedEvent, OnServiceTabTapped);
        ServiceTabs.AddHandler(DoubleTappedEvent, OnServiceTabTapped);
        ServiceTabs.AddHandler(KeyDownEvent, OnServiceTabKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnServiceTabTapped(object? sender, RoutedEventArgs args)
    {
        // Selection loads new tabs; tapping the current tab refreshes it once.
        if (DataContext is OnlineViewModel model && args.Source is StyledElement { DataContext: ProviderOption option }
                                                 && option.Id == _serviceBeforePress &&
                                                 option.Id == model.SelectedService?.Id && model.CanBrowseRemote)
            model.RemotePlaylistsCommand.Execute(null);
    }

    private async void OnServiceTabKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key is not (Key.Enter or Key.Space) ||
            DataContext is not OnlineViewModel { CanBrowseRemote: true } model) return;
        args.Handled = true;
        await model.RemotePlaylistsCommand.ExecuteAsync(null);
        if (IsEffectivelyVisible) ServiceTabs.ContainerFromIndex(ServiceTabs.SelectedIndex)?.Focus();
    }
}