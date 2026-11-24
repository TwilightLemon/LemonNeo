using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using LemonNeo.Desktop.ViewModels.Tools;

namespace LemonNeo.Desktop.Views.Playback;

public partial class DecodeProfileView : UserControl
{
    public DecodeProfileView() => InitializeComponent();

    private async void OnEqualizer(object? sender, RoutedEventArgs args)
    {
        var owner = TopLevel.GetTopLevel(this) as Window ?? this.FindLogicalAncestorOfType<Window>() ??
                    (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null || DataContext is not ProfileEditorViewModel editor) return;
        editor.SetDialogOpen(true);
        try { await new EqualizerDialog { DataContext = editor }.ShowDialog(owner); }
        finally { editor.SetDialogOpen(false); }
    }
}
