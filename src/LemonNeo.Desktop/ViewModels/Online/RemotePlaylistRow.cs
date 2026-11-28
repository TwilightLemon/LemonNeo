using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.ViewModels.Online;

public sealed record RemotePlaylistRow(string ProviderId, string? Id, string Name, string Collection, string TrackCount)
{
    public Uri? ArtworkUri { get; init; }
    public ArtworkService? ArtworkLoader { get; init; }
    public bool CanImport => Id is not null;
}
