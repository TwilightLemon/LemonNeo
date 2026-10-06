using LemonNeo.Core.Library;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LemonNeo.Desktop.ViewModels.Library;

public sealed partial class PlaylistDialogViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _name = "";

    [ObservableProperty]
    private string? _description;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolder))]
    private string? _folderPath;

    public bool IsNew { get; }
    public bool CanSave => !string.IsNullOrWhiteSpace(Name);
    public bool HasFolder => FolderPath is not null;

    public PlaylistDialogViewModel(Playlist? playlist = null)
    {
        IsNew = playlist is null;
        Name = playlist?.Name ?? "";
        Description = playlist?.Description;
    }

    public void SelectFolder(string directoryPath)
    {
        if (!IsNew) return;
        var folder = new DirectoryInfo(directoryPath);
        FolderPath = folder.FullName;
        Name = string.IsNullOrEmpty(folder.Name) ? folder.FullName : folder.Name;
    }
}
