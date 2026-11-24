using CommunityToolkit.Mvvm.ComponentModel;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.ViewModels.Shared;

public sealed class TrackRow(
    Track track,
    int number,
    LocalizationService text,
    Guid? QueueItemId = null,
    Guid? PlaylistId = null,
    Guid? PlaylistItemId = null,
    PlaylistEntryId? playbackEntry = null) : ObservableObject
{
    private bool _isCurrent;
    private bool _isMarked;
    private bool _isSelectionMode;
    public Track Track { get; private set; } = track;
    public int Number { get; private set; } = number;
    public LocalizationService Text { get; } = text;
    public Guid? QueueItemId { get; } = QueueItemId;
    public Guid? PlaylistId { get; } = PlaylistId;
    public Guid? PlaylistItemId { get; } = PlaylistItemId;

    public PlaylistEntryId? PlaybackEntry { get; } = playbackEntry ??
        (PlaylistId is { } list && PlaylistItemId is { } item ? new PlaylistEntryId(list, item) : null);

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    public bool IsMarked
    {
        get => _isMarked;
        set => SetProperty(ref _isMarked, value);
    }

    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        set
        {
            if (!SetProperty(ref _isSelectionMode, value)) return;
            OnPropertyChanged(nameof(HasArtistLinks));
            OnPropertyChanged(nameof(HasAlbumLink));
        }
    }

    public Uri? ArtworkUri => Track.ArtworkUri;
    public ArtworkService? ArtworkLoader { get; set; }
    internal Func<MusicItemId, bool>? CanBrowseCatalog { get; set; }
    internal Func<Track, MusicItemId?>? FindCatalogTrack { get; set; }
    public string Title => Track.Title;
    public string Identity => Track.Id.ToString();

    public string Artists => Track.Artists.Count > 0
        ? string.Join(" / ", Track.Artists.Select(a => a.Name))
        : Text[LanguageManager.Keys.UnknownArtist];

    public string Album => Track.Album?.Title ?? Text[LanguageManager.Keys.UnknownAlbum];
    public CatalogItem? AlbumLink => Track.Album is { } album
        ? GetCatalogLink(SearchKind.Album, album.Id, album.Title, Track.ArtworkUri) : null;
    public IReadOnlyList<ArtistLink> ArtistLinks => Track.Artists.Select(artist =>
        new ArtistLink(artist.Name, GetCatalogLink(SearchKind.Artist, artist.Id, artist.Name))).ToArray();
    public bool HasArtistLinks => !IsSelectionMode &&
        Track.Artists.Any(artist => GetCatalogLink(SearchKind.Artist, artist.Id, artist.Name) is not null);
    public bool HasAlbumLink => !IsSelectionMode && AlbumLink is not null;
    public string Duration => Track.Duration is { } value ? FormatTime(value) : "—";

    private CatalogItem? GetCatalogLink(SearchKind kind, MusicItemId? direct, string name, Uri? artwork = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (direct is { } id && CanBrowseCatalog?.Invoke(id) == true) return new CatalogItem(kind, id, name, artwork);
        return FindCatalogTrack?.Invoke(Track) is { } track
            ? new CatalogItem(kind, track, name, artwork, ResolveFromTrack: true)
            : null;
    }

    internal void Update(Track track, int number)
    {
        if (Number != number)
        {
            Number = number;
            OnPropertyChanged(nameof(Number));
        }
        if (Equals(Track, track)) return;
        var artworkUri = ArtworkUri;
        Track = track;
        OnPropertyChanged(nameof(Track));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Identity));
        OnPropertyChanged(nameof(ArtistLinks));
        OnPropertyChanged(nameof(HasArtistLinks));
        OnPropertyChanged(nameof(AlbumLink));
        OnPropertyChanged(nameof(HasAlbumLink));
        RefreshLocalizedProperties();
        if (!Equals(artworkUri, ArtworkUri)) OnPropertyChanged(nameof(ArtworkUri));
    }

    internal void RefreshLocalizedProperties()
    {
        OnPropertyChanged(nameof(Artists));
        OnPropertyChanged(nameof(Album));
        OnPropertyChanged(nameof(Duration));
    }

    public static string FormatTime(TimeSpan time)
    {
        return time.TotalHours >= 1
            ? $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : time.ToString(@"m\:ss");
    }
}

public sealed record ArtistLink(string Name, CatalogItem? Item)
{
    public bool CanOpen => Item is not null;
}
