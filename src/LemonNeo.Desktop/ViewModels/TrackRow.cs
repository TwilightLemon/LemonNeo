using LemonNeo.Core;
using LemonNeo.Desktop.Infrastructure;

namespace LemonNeo.Desktop.ViewModels;

public sealed record TrackRow(Track Track, int Number, LocalizationService Text, Guid? QueueItemId = null, Guid? PlaylistId = null, Guid? PlaylistItemId = null)
{
    public string Title => Track.Title;
    public string Identity => Track.Id.ToString();
    public string Artists => Track.Artists.Count > 0 ? string.Join(" / ", Track.Artists.Select(a => a.Name)) : Text["UnknownArtist"];
    public string Album => Track.Album?.Title ?? Text["UnknownAlbum"];
    public string Duration => Track.Duration is { } value ? FormatTime(value) : "—";
    public static string FormatTime(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
}
