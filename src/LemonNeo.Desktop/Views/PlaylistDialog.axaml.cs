using LemonNeo.Core.Library;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.ViewModels;

namespace LemonNeo.Desktop.Views;

public partial class PlaylistDialog : Window
{
    public PlaylistDialog() : this(null) { }
    public PlaylistDialog(Playlist? playlist, bool importFolderOnOpen = false)
    {
        InitializeComponent();
        DataContext = new PlaylistDialogViewModel(playlist);
        if (playlist is not null)
        {
            this.Bind(TitleProperty, LanguageManager.Instance.EditPlaylist);
            DialogHeading.Bind(TextBlock.TextProperty, LanguageManager.Instance.EditPlaylist);
            CreateButton.Bind(ContentControl.ContentProperty, LanguageManager.Instance.Save);
        }
        Opened += async (_, _) =>
        {
            ImportFolderButton.IsEnabled = StorageProvider.CanPickFolder;
            PlaylistName.Focus();
            if (playlist is null && importFolderOnOpen) await SelectFolderAsync();
        };
    }

    private async void OnImportFolder(object? sender, RoutedEventArgs args) => await SelectFolderAsync();

    private async Task SelectFolderAsync()
    {
        if (DataContext is not PlaylistDialogViewModel { IsNew: true } model || !StorageProvider.CanPickFolder) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = ((Irihi.Lingua.LinguaObservableString)LanguageManager.Instance.ChooseFolder).CurrentValue,
            AllowMultiple = false
        });
        foreach (var folder in folders)
        {
            using (folder)
                if (folder.TryGetLocalPath() is { } path) model.SelectFolder(path);
        }
        PlaylistName.Focus();
    }

    private void OnCreate(object? sender, RoutedEventArgs args)
    {
        if (DataContext is PlaylistDialogViewModel { CanSave: true } model)
            Close(new PlaylistEditResult(model.Name.Trim(),
                string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim(), model.FolderPath));
    }
    private void OnCancel(object? sender, RoutedEventArgs args) => Close(null);
}
public sealed record PlaylistEditResult(string Name, string? Description, string? FolderPath = null);
