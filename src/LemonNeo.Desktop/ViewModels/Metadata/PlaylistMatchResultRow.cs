using CommunityToolkit.Mvvm.Input;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Music;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.ViewModels.Metadata;

public sealed record PlaylistTrackChangeRow(string Field, string Before, string After);

public sealed class PlaylistMatchResultRow
{
    private readonly LocalizationService _text;

    public PlaylistMatchResultRow(
        int number,
        Guid playlistItemId,
        Track original,
        Track updated,
        MetadataLookupResult lookup,
        LocalizationService text,
        RelayCommand<PlaylistMatchResultRow> openTrackCommand)
    {
        Number = number;
        PlaylistItemId = playlistItemId;
        Original = original;
        Updated = updated;
        Lookup = lookup;
        _text = text;
        OpenTrackCommand = openTrackCommand;
        Changes = BuildChanges();
    }

    public int Number { get; }
    public Guid PlaylistItemId { get; }
    public Track Original { get; }
    public Track Updated { get; }
    public MetadataLookupResult Lookup { get; }
    public RelayCommand<PlaylistMatchResultRow> OpenTrackCommand { get; }
    public IReadOnlyList<PlaylistTrackChangeRow> Changes { get; }
    public bool IsSuccessful => Lookup.Matches.Count > 0;
    public bool HasChanges => Changes.Count > 0;
    public string Title => Original.Title;
    public string Artists => Original.Artists.Count == 0
        ? _text["UnknownArtist"]
        : string.Join(" / ", Original.Artists.Select(artist => artist.Name));
    public string Identity => Original.Id.ToString();
    public string Result => _text[IsSuccessful ? "MatchSucceeded" : "MatchFailed"];
    public string Details => IsSuccessful
        ? HasChanges ? _text.Format("MatchChangedFields", Changes.Count) : _text["NoMetadataChanges"]
        : _text["NoReliableMatch"];
    public string MatchSource => Lookup.Matches.FirstOrDefault() is { } match
        ? _text.Format("MatchSourceSummary", match.SourceNamespace, match.Confidence)
        : "";
    public string OpenTrackHint => _text["OpenTrackDetails"];

    public PlaylistMatchResultRow Relocalize(LocalizationService text) =>
        new(Number, PlaylistItemId, Original, Updated, Lookup, text, OpenTrackCommand);

    private IReadOnlyList<PlaylistTrackChangeRow> BuildChanges()
    {
        var changes = new List<PlaylistTrackChangeRow>();
        Add(changes, "TrackTitle", Original.Title, Updated.Title, StringComparer.Ordinal.Equals(Original.Title, Updated.Title));
        Add(changes, "ArtistsLabel", FormatArtists(Original.Artists), FormatArtists(Updated.Artists), Original.Artists.SequenceEqual(Updated.Artists));
        Add(changes, "AlbumLabel", FormatAlbum(Original.Album), FormatAlbum(Updated.Album), Equals(Original.Album, Updated.Album));
        Add(changes, "Duration", FormatDuration(Original.Duration), FormatDuration(Updated.Duration), Original.Duration == Updated.Duration);
        Add(changes, "ArtworkUriLabel", FormatValue(Original.ArtworkUri?.ToString()), FormatValue(Updated.ArtworkUri?.ToString()), Equals(Original.ArtworkUri, Updated.ArtworkUri));
        Add(changes, "ExternalIds", FormatExternalIds(Original.ExternalIds), FormatExternalIds(Updated.ExternalIds), Original.ExternalIds.SequenceEqual(Updated.ExternalIds));
        return changes;
    }

    private void Add(List<PlaylistTrackChangeRow> changes, string fieldKey, string before, string after, bool equal)
    {
        if (!equal) changes.Add(new(_text[fieldKey], before, after));
    }

    private static string FormatArtists(IReadOnlyList<ArtistRef> artists) => artists.Count == 0
        ? "—"
        : string.Join(Environment.NewLine, artists.Select(artist => artist.Id is { } id ? $"{artist.Name} [{id}]" : artist.Name));

    private static string FormatAlbum(AlbumRef? album) => album is null
        ? "—"
        : album.Id is { } id ? $"{album.Title} [{id}]" : album.Title;

    private static string FormatDuration(TimeSpan? duration) => duration is { } value ? TrackRow.FormatTime(value) : "—";

    private static string FormatExternalIds(IReadOnlyList<ExternalMusicId> identifiers) => identifiers.Count == 0
        ? "—"
        : string.Join(Environment.NewLine, identifiers.Select(id => $"{id.Namespace}:{id.Kind ?? "-"} = {id.Value}"));

    private static string FormatValue(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
}
