using CommunityToolkit.Mvvm.ComponentModel;
using LemonNeo.Core.Library;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.ViewModels.Shell;

public sealed class PlaylistMenuItemViewModel : ObservableObject
{
    private string _name = "";
    private int _trackCount;
    private Uri? _artworkUri;

    public PlaylistMenuItemViewModel(Playlist playlist, Uri? artworkUri, ArtworkService artworkLoader)
    {
        Id = playlist.Id;
        ArtworkLoader = artworkLoader;
        Update(playlist, artworkUri);
    }

    public Guid Id { get; }
    public ArtworkService ArtworkLoader { get; }

    public Uri? ArtworkUri
    {
        get => _artworkUri;
        private set => SetProperty(ref _artworkUri, value);
    }

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    public int TrackCount
    {
        get => _trackCount;
        private set => SetProperty(ref _trackCount, value);
    }

    public void Update(Playlist playlist, Uri? artworkUri)
    {
        ArtworkUri = artworkUri;
        Name = playlist.Name;
        TrackCount = playlist.Items.Count;
    }
}