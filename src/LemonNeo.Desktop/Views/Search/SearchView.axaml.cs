using Avalonia.Controls;
using Avalonia.Input;
using LemonNeo.Desktop.Controls.Shared;
using LemonNeo.Desktop.ViewModels.Search;

namespace LemonNeo.Desktop.Views.Search;

public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        TrackSelectionBehavior.Attach(TrackList);
    }

    private async void OnTrackDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (DataContext is SearchViewModel { IsSelectionMode: false, SelectedTrack: { } track } vm)
            await vm.PlayAsync(track);
    }

    private async void OnTrackKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter && DataContext is SearchViewModel
            {
                IsSelectionMode: false, SelectedTrack: { } track
            } vm)
        {
            args.Handled = true;
            await vm.PlayAsync(track);
        }
    }
}