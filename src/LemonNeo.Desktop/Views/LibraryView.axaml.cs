using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LemonNeo.Desktop.ViewModels;

namespace LemonNeo.Desktop.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();
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
        if (DataContext is MainViewModel { SelectedTrack: { } track } vm) await vm.PlayAsync(track);
    }
    private async void OnTrackKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter && DataContext is MainViewModel { SelectedTrack: { } track } vm)
        {
            args.Handled = true;
            await vm.PlayAsync(track);
        }
    }
}
