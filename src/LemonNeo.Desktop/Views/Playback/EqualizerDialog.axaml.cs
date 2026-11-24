using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LemonNeo.Desktop.Views.Playback;

public partial class EqualizerDialog : Window
{
    public EqualizerDialog() => InitializeComponent();
    private void OnDone(object? sender, RoutedEventArgs args) => Close();
}
