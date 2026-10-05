using CommunityToolkit.Mvvm.ComponentModel;
using LemonNeo.Core;

namespace LemonNeo.Desktop.ViewModels;

public sealed class PlaylistMenuItemViewModel : ObservableObject
{
    private string _name = "";
    private int _trackCount;

    public PlaylistMenuItemViewModel(Playlist playlist)
    {
        Id = playlist.Id;
        Update(playlist);
    }

    public Guid Id { get; }
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public int TrackCount { get => _trackCount; private set => SetProperty(ref _trackCount, value); }

    public void Update(Playlist playlist)
    {
        Name = playlist.Name;
        TrackCount = playlist.Items.Count;
    }
}
