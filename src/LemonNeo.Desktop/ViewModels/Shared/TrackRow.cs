using CommunityToolkit.Mvvm.ComponentModel;
using LemonNeo.Core.Music;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.ViewModels.Shared;

public sealed class TrackRow(
    Track track,
    int number,
    LocalizationService text,
    Guid? QueueItemId = null,
    Guid? PlaylistId = null,
    Guid? PlaylistItemId = null) : ObservableObject
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
        set => SetProperty(ref _isSelectionMode, value);
    }

    public Uri? ArtworkUri => Track.ArtworkUri;
    public ArtworkService? ArtworkLoader { get; set; }
    public string Title => Track.Title;
    public string Identity => Track.Id.ToString();

    public string Artists => Track.Artists.Count > 0
        ? string.Join(" / ", Track.Artists.Select(a => a.Name))
        : Text[LanguageManager.Keys.UnknownArtist];

    public string Album => Track.Album?.Title ?? Text[LanguageManager.Keys.UnknownAlbum];
    public string Duration => Track.Duration is { } value ? FormatTime(value) : "—";

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
