using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.Controls.Shared;

public partial class AddToPlaylistButton : UserControl
{
    public static readonly StyledProperty<bool> IsGhostProperty =
        AvaloniaProperty.Register<AddToPlaylistButton, bool>(nameof(IsGhost));

    public static readonly StyledProperty<bool> IsCompactProperty =
        AvaloniaProperty.Register<AddToPlaylistButton, bool>(nameof(IsCompact));

    public AddToPlaylistButton()
    {
        InitializeComponent();
    }

    public bool IsGhost
    {
        get => GetValue(IsGhostProperty);
        set => SetValue(IsGhostProperty, value);
    }

    public bool IsCompact
    {
        get => GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    private async void OnAdd(object? sender, RoutedEventArgs args)
    {
        if (DataContext is TrackListViewModel vm && await vm.AddSelectedTracksAsync())
            AddButton.Flyout?.Hide();
    }
}