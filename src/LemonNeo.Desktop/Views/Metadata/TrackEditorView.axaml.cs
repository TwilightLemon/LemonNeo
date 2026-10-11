using Avalonia.Controls;
using Avalonia.Interactivity;
using LemonNeo.Desktop.ViewModels.Metadata;

namespace LemonNeo.Desktop.Views.Metadata;

public partial class TrackEditorView : UserControl
{
    public TrackEditorView()
    {
        InitializeComponent();
    }

    private void OnRemoveExternalId(object? sender, RoutedEventArgs args)
    {
        if (DataContext is TrackEditorPageViewModel { CanInteract: true, Editor: { } editor } &&
            sender is Control { DataContext: ExternalIdEditor row })
            editor.RemoveExternalIdCommand.Execute(row);
    }
}