using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LemonNeo.Core.Library;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Library;

namespace LemonNeo.Desktop.Views.Library;

public partial class PlaylistDialog : Window
{
    public PlaylistDialog() : this(null)
    {
    }

    public PlaylistDialog(Playlist? playlist, bool importFolderOnOpen = false, ArtworkService? artworkLoader = null,
        Uri? artworkUri = null, bool canEditDetails = true)
    {
        InitializeComponent();
        DataContext = new PlaylistDialogViewModel(playlist, artworkLoader, artworkUri, canEditDetails);
        if (playlist is not null)
        {
            Bind(TitleProperty, LanguageManager.Instance.EditPlaylist);
            DialogHeading.Bind(TextBlock.TextProperty, LanguageManager.Instance.EditPlaylist);
            CreateButton.Bind(ContentProperty, LanguageManager.Instance.Save);
        }

        Opened += async (_, _) =>
        {
            ImportFolderButton.IsEnabled = StorageProvider.CanPickFolder;
            ChooseArtworkButton.IsEnabled = StorageProvider.CanOpen;
            PlaylistName.Focus();
            if (playlist is null && importFolderOnOpen) await SelectFolderAsync();
        };
    }

    private async void OnImportFolder(object? sender, RoutedEventArgs args)
    {
        await SelectFolderAsync();
    }

    private async Task SelectFolderAsync()
    {
        if (DataContext is not PlaylistDialogViewModel { IsNew: true } model || !StorageProvider.CanPickFolder) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LanguageManager.GetCurrentValue(LanguageManager.Keys.ChooseFolder),
            AllowMultiple = false
        });
        foreach (var folder in folders)
            using (folder)
            {
                if (folder.TryGetLocalPath() is { } path) model.SelectFolder(path);
            }

        PlaylistName.Focus();
    }

    private async void OnChooseArtwork(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not PlaylistDialogViewModel
            { IsNew: false, IsChoosingArtwork: false, ArtworkLoader: { } loader } model || !StorageProvider.CanOpen)
            return;

        model.IsChoosingArtwork = true;
        model.ArtworkError = null;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LanguageManager.GetCurrentValue(LanguageManager.Keys.ChoosePlaylistCover),
                AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.ImageAll]
            });
            using var file = files.FirstOrDefault();
            if (file is null) return;
            if (file.TryGetLocalPath() is { } path)
            {
                var uri = new Uri(path);
                using var image = await loader.LoadWithDataAsync(uri);
                if (image is not null)
                {
                    model.SetArtwork(uri);
                    return;
                }
            }
            model.ArtworkError = LanguageManager.GetCurrentValue(LanguageManager.Keys.PlaylistCoverUnavailable);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            model.ArtworkError = LanguageManager.GetCurrentValue(LanguageManager.Keys.PlaylistCoverUnavailable);
        }
        finally
        {
            model.IsChoosingArtwork = false;
        }
    }

    private void OnRemoveArtwork(object? sender, RoutedEventArgs args)
    {
        if (DataContext is PlaylistDialogViewModel { IsNew: false, IsChoosingArtwork: false } model)
            model.SetArtwork(null);
    }

    private void OnCreate(object? sender, RoutedEventArgs args)
    {
        if (DataContext is PlaylistDialogViewModel { CanSave: true } model)
            Close(new PlaylistEditResult(model.Name.Trim(),
                string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim(), model.FolderPath,
                model.HasFolder && model.Synchronize, model.ArtworkChanged,
                model.ArtworkChanged ? model.ArtworkUri : null));
    }

    private void OnCancel(object? sender, RoutedEventArgs args)
    {
        Close(null);
    }
}
