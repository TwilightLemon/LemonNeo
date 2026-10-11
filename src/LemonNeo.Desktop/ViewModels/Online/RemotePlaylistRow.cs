namespace LemonNeo.Desktop.ViewModels.Online;

public sealed record RemotePlaylistRow(string ProviderId, string? Id, string Name, string Collection, string TrackCount)
{
    public bool CanImport => Id is not null;
}