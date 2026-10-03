using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.ViewModels;

namespace LemonNeo.Desktop.Views;
public partial class ToolsView : UserControl
{
    public ToolsView() => InitializeComponent();
    private async void OnImportScript(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainViewModel { CanInteract: true } vm || TopLevel.GetTopLevel(this) is not { StorageProvider.CanOpen: true } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = ((Irihi.Lingua.LinguaObservableString)LanguageManager.Instance.ImportScript).CurrentValue, AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(((Irihi.Lingua.LinguaObservableString)LanguageManager.Instance.ScriptFiles).CurrentValue)
                { Patterns = ["*.txt", "*.script"] }, FilePickerFileTypes.All]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await vm.ImportStartupScriptAsync(path);
    }
}
