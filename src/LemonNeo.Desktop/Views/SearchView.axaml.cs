using Avalonia.Controls;
using Avalonia.Input;
using LemonNeo.Desktop.ViewModels;

namespace LemonNeo.Desktop.Views;

public partial class SearchView : UserControl
{
    public SearchView() => InitializeComponent();
    private async void OnTrackDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (DataContext is SearchViewModel { SelectedTrack: { } track } vm) await vm.PlayAsync(track);
    }
    private async void OnTrackKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter && DataContext is SearchViewModel { SelectedTrack: { } track } vm)
        {
            args.Handled = true;
            await vm.PlayAsync(track);
        }
    }
}
