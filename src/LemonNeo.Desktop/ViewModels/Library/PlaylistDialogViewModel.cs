using CommunityToolkit.Mvvm.ComponentModel;
using LemonNeo.Core.Library;

namespace LemonNeo.Desktop.ViewModels.Library;

public sealed partial class PlaylistDialogViewModel : ObservableObject
{
    [ObservableProperty] private string? _description;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFolder))]
    private string? _folderPath;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _name = "";

    public PlaylistDialogViewModel(Playlist? playlist = null)
    {
        IsNew = playlist is null;
        Name = playlist?.Name ?? "";
        Description = playlist?.Description;
    }

    public bool IsNew { get; }
    public bool CanSave => !string.IsNullOrWhiteSpace(Name);
    public bool HasFolder => FolderPath is not null;

    public void SelectFolder(string directoryPath)
    {
        if (!IsNew) return;
        var folder = new DirectoryInfo(directoryPath);
        FolderPath = folder.FullName;
        Name = string.IsNullOrEmpty(folder.Name) ? folder.FullName : folder.Name;
    }
}