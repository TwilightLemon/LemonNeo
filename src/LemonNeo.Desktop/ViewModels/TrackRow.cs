using LemonNeo.Core;
using LemonNeo.Desktop.Infrastructure;

namespace LemonNeo.Desktop.ViewModels;

public sealed record TrackRow(Track Track, int Number, LocalizationService Text, Guid? QueueItemId = null, Guid? PlaylistId = null, Guid? PlaylistItemId = null)
{
    public Uri? ArtworkUri => Track.ArtworkUri;
    public ArtworkService? ArtworkLoader { get; init; }
    public string Title => Track.Title;
    public string Identity => Track.Id.ToString();
    public string Artists => Track.Artists.Count > 0 ? string.Join(" / ", Track.Artists.Select(a => a.Name)) : Text["UnknownArtist"];
    public string Album => Track.Album?.Title ?? Text["UnknownAlbum"];
    public string Duration => Track.Duration is { } value ? FormatTime(value) : "—";
    public static string FormatTime(TimeSpan time) => time.TotalHours >= 1
        ? $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
        : time.ToString(@"m\:ss");
}
