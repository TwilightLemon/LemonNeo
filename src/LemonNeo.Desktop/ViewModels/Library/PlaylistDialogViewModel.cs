using CommunityToolkit.Mvvm.ComponentModel;
using LemonNeo.Core.Library;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.ViewModels.Library;

public sealed partial class PlaylistDialogViewModel : ObservableObject
{
    [ObservableProperty] private string? _description;
    [ObservableProperty] private bool _synchronize;
    [ObservableProperty] private string? _artworkError;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasArtwork))]
    private Uri? _artworkUri;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isChoosingArtwork;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFolder))]
    private string? _folderPath;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _name = "";

    public PlaylistDialogViewModel(Playlist? playlist = null, ArtworkService? artworkLoader = null,
        Uri? artworkUri = null, bool canEditDetails = true)
    {
        IsNew = playlist is null;
        ArtworkLoader = artworkLoader;
        ArtworkUri = artworkUri;
        CanEditDetails = canEditDetails;
        Name = playlist?.Name ?? "";
        Description = playlist?.Description;
    }

    public bool IsNew { get; }
    public bool CanEditDetails { get; }
    public ArtworkService? ArtworkLoader { get; }
    public bool HasArtwork => ArtworkUri is not null;
    public bool ArtworkChanged { get; private set; }
    public bool CanSave => !IsChoosingArtwork && !string.IsNullOrWhiteSpace(Name);
    public bool HasFolder => FolderPath is not null;

    public void SetArtwork(Uri? file)
    {
        ArtworkUri = file;
        ArtworkChanged = true;
        ArtworkError = null;
    }

    public void SelectFolder(string directoryPath)
    {
        if (!IsNew) return;
        var folder = new DirectoryInfo(directoryPath);
        FolderPath = folder.FullName;
        Name = string.IsNullOrEmpty(folder.Name) ? folder.FullName : folder.Name;
    }
}
