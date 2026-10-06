namespace LemonNeo.Desktop.ViewModels.Online;

public sealed record RemotePlaylistCategory(string Id, string Name, IReadOnlyList<RemotePlaylistRow> Playlists);
