using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LemonNeo.Application.Library;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.Views.Shared;

namespace LemonNeo.Desktop.Views.Library;

public partial class TrackExportDialog : Window
{
    public TrackExportDialog() : this(new TrackExportPreferences())
    {
    }

    public TrackExportDialog(TrackExportPreferences preferences)
    {
        InitializeComponent();
        FolderPath.Text = preferences.Directory ?? TrackExportPreferences.DefaultDirectory;
        FileNameFormat.Text = preferences.FileNameFormat;
        WriteLyrics.IsChecked = preferences.WriteLyrics;
        WriteTags.IsChecked = preferences.WriteTags;
        Opened += (_, _) => ChooseFolderButton.IsEnabled = StorageProvider.CanPickFolder;
    }

    private async void OnChooseFolder(object? sender, RoutedEventArgs args)
    {
        try
        {
            var path = FolderPath.Text!;
            while (!Directory.Exists(path) && Path.GetDirectoryName(path) is { } parent) path = parent;
            using var start = Directory.Exists(path) ? await StorageProvider.TryGetFolderFromPathAsync(path) : null;
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = LanguageManager.GetCurrentValue(LanguageManager.Keys.ExportFolder),
                SuggestedStartLocation = start,
                AllowMultiple = false
            });
            foreach (var folder in folders)
                using (folder)
                {
                    if (folder.TryGetLocalPath() is { } localPath) FolderPath.Text = localPath;
                }
        }
        catch (Exception exception)
        {
            await new MessageDialog(Title ?? "", exception.Message, LanguageManager.GetCurrentValue(LanguageManager.Keys.OK))
                .ShowDialog(this);
        }
    }

    private void OnExport(object? sender, RoutedEventArgs args)
    {
        var format = FileNameFormat.Text?.Trim();
        Close(new TrackExportPreferences(FolderPath.Text, WriteLyrics.IsChecked == true, WriteTags.IsChecked == true,
            string.IsNullOrEmpty(format) ? TrackExportService.DefaultFileNameFormat : format));
    }

    private void OnCancel(object? sender, RoutedEventArgs args) => Close(null);
}
