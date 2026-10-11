using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Irihi.Lingua;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Settings;

namespace LemonNeo.Desktop.Views.Settings;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private async void OnChangeLibraryPath(object? sender, RoutedEventArgs args)
    {
        var path = await PickFolderAsync(
            ((LinguaObservableString)LanguageManager.Instance.ChooseLibraryPath).CurrentValue ??
            "Choose a music library folder");
        if (path is not null && DataContext is SettingsViewModel model) await model.ChangeLibraryAsync(path);
    }

    private async void OnChangeCachePath(object? sender, RoutedEventArgs args)
    {
        var path = await PickFolderAsync(
            ((LinguaObservableString)LanguageManager.Instance.ChooseCachePath).CurrentValue ?? "Choose a cache folder");
        if (path is not null && DataContext is SettingsViewModel model) await model.ChangeCacheAsync(path);
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider?.CanPickFolder != true) return null;
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        foreach (var folder in folders)
            using (folder)
            {
                if (folder.TryGetLocalPath() is { } path) return path;
            }

        return null;
    }
}