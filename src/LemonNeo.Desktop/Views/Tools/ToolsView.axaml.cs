using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LemonNeo.Desktop.Views.Tools;

public partial class ToolsView : UserControl
{
    public ToolsView()
    {
        InitializeComponent();
    }

    private void OnDependencyWebsiteClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is not Control { Tag: string address } ||
            !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https")) return;

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}