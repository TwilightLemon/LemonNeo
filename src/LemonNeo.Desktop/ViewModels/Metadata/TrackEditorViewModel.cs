using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Core.Music;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.ViewModels.Metadata;

public sealed partial class TrackEditorViewModel : ObservableObject
{
    private readonly LocalizationService _text;
    [ObservableProperty] private string _album = "";
    [ObservableProperty] private string _artists = "";
    [ObservableProperty] private string _artworkUri = "";
    [ObservableProperty] private string _durationSeconds = "";
    [ObservableProperty] private string _providerId = "";
    private Track _snapshot;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _trackId = "";

    public TrackEditorViewModel(Track track, LocalizationService text, Guid? playlistId = null, Guid? itemId = null,
        bool isNew = false)
    {
        _snapshot = track;
        _text = text;
        PlaylistId = playlistId;
        ItemId = itemId;
        IsNew = isNew;
        Apply(track);
    }

    public Guid? PlaylistId { get; private set; }
    public Guid? ItemId { get; private set; }
    public bool IsNew { get; }
    public bool CanUpdate => PlaylistId is not null && ItemId is not null;
    public string ItemIdentity => ItemId?.ToString() ?? "—";
    public ObservableCollection<ExternalIdEditor> ExternalIds { get; } = [];

    public void Apply(Track track)
    {
        _snapshot = track;
        Title = track.Title;
        Artists = string.Join("; ", track.Artists.Select(a => a.Name));
        Album = track.Album?.Title ?? "";
        DurationSeconds = track.Duration?.TotalSeconds.ToString("0.#######", _text.Culture) ?? "";
        ArtworkUri = track.ArtworkUri?.ToString() ?? "";
        ProviderId = track.Id.ProviderId;
        TrackId = track.Id.RemoteId;
        ExternalIds.Clear();
        foreach (var id in track.ExternalIds)
            ExternalIds.Add(new ExternalIdEditor { Namespace = id.Namespace, Kind = id.Kind ?? "", Value = id.Value });
    }

    public void ApplyMetadata(Track track)
    {
        _snapshot = _snapshot with { Artists = track.Artists, Album = track.Album };
        Title = track.Title;
        Artists = string.Join("; ", track.Artists.Select(artist => artist.Name));
        Album = track.Album?.Title ?? "";
        DurationSeconds = track.Duration?.TotalSeconds.ToString("0.#######", _text.Culture) ?? "";
        if (track.ArtworkUri is not null) ArtworkUri = track.ArtworkUri.ToString();
    }

    public int AddExternalIds(IEnumerable<ExternalMusicId> identifiers)
    {
        var incoming = identifiers
            .Where(id => !string.IsNullOrWhiteSpace(id.Namespace) && !string.IsNullOrWhiteSpace(id.Value))
            .Select(id => new ExternalMusicId(id.Namespace.Trim(), id.Value.Trim(), NormalizeKind(id.Kind)))
            .DistinctBy(IdIdentity).ToArray();
        var existing = ExternalIds.Select(row =>
            IdIdentity(new ExternalMusicId((row.Namespace ?? "").Trim(), (row.Value ?? "").Trim(),
                NormalizeKind(row.Kind)))).ToHashSet();
        var added = 0;
        foreach (var id in incoming)
        {
            if (!existing.Add(IdIdentity(id))) continue;
            ExternalIds.Add(new ExternalIdEditor { Namespace = id.Namespace, Kind = id.Kind ?? "", Value = id.Value });
            added++;
        }

        // Provider-supplied alternatives remain valid when several IDs share the same namespace and kind.
        _snapshot = _snapshot with
        {
            ExternalIds = _snapshot.ExternalIds.Concat(incoming).DistinctBy(IdIdentity).ToArray()
        };
        return added;
    }

    private static string? NormalizeKind(string? kind)
    {
        return string.IsNullOrWhiteSpace(kind) || kind.Trim() == "-" ? null : kind.Trim();
    }

    private static (string, string?, string) IdIdentity(ExternalMusicId id)
    {
        return (id.Namespace.ToUpperInvariant(), id.Kind?.ToUpperInvariant(), id.Value);
    }

    internal void AttachItem(Guid playlistId, Guid itemId)
    {
        PlaylistId = playlistId;
        ItemId = itemId;
        OnPropertyChanged(nameof(PlaylistId));
        OnPropertyChanged(nameof(ItemId));
        OnPropertyChanged(nameof(ItemIdentity));
        OnPropertyChanged(nameof(CanUpdate));
    }

    public Track Build(bool validateExternalIdSchemes = true)
    {
        if (string.IsNullOrWhiteSpace(Title) || string.IsNullOrWhiteSpace(ProviderId) ||
            string.IsNullOrWhiteSpace(TrackId))
            throw new ArgumentException(_text["TrackFieldsRequired"]);
        TimeSpan? duration = null;
        if (!string.IsNullOrWhiteSpace(DurationSeconds))
        {
            if (!(double.TryParse(DurationSeconds, NumberStyles.Float, _text.Culture, out var seconds) ||
                  double.TryParse(DurationSeconds, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) ||
                !double.IsFinite(seconds) || seconds < 0 || seconds >= TimeSpan.MaxValue.TotalSeconds)
                throw new ArgumentException(_text["DurationInvalid"]);
            duration = TimeSpan.FromSeconds(seconds);
        }

        Uri? artwork = null;
        if (!string.IsNullOrWhiteSpace(ArtworkUri) && !Uri.TryCreate(ArtworkUri.Trim(), UriKind.Absolute, out artwork))
            throw new ArgumentException(_text["ArtworkInvalid"]);
        var ids = new List<ExternalMusicId>();
        foreach (var row in ExternalIds)
        {
            if (string.IsNullOrWhiteSpace(row.Namespace) || string.IsNullOrWhiteSpace(row.Value))
                throw new ArgumentException(_text["ExternalIdInvalid"]);
            var kind = NormalizeKind(row.Kind);
            ids.Add(new ExternalMusicId(row.Namespace.Trim(), row.Value.Trim(), kind));
        }

        static (string, string?) Scheme(ExternalMusicId id)
        {
            return (id.Namespace.ToUpperInvariant(), id.Kind?.ToUpperInvariant());
        }

        if (validateExternalIdSchemes)
            // Metadata providers may supply multiple known IDs for a scheme. Retain
            // those alternatives while rejecting duplicate schemes introduced by hand.
            foreach (var group in ids.GroupBy(Scheme).Where(g => g.Count() > 1))
            {
                var retained = _snapshot.ExternalIds.Where(id => Scheme(id) == group.Key)
                    .GroupBy(id => id.Value).ToDictionary(g => g.Key, g => g.Count());
                if (group.GroupBy(id => id.Value).Any(g => g.Count() > retained.GetValueOrDefault(g.Key)))
                    throw new ArgumentException(_text["DuplicateExternalId"]);
            }

        return _snapshot with
        {
            Id = new MusicItemId(ProviderId.Trim(), TrackId.Trim()), Title = Title.Trim(),
            Artists = Artists.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(name => _snapshot.Artists.FirstOrDefault(a => a.Name == name) ?? new ArtistRef { Name = name })
                .ToArray(),
            Album = string.IsNullOrWhiteSpace(Album) ? null :
            _snapshot.Album?.Title == Album.Trim() ? _snapshot.Album : new AlbumRef { Title = Album.Trim() },
            Duration = duration, ArtworkUri = artwork, ExternalIds = ids
        };
    }

    [RelayCommand]
    private void AddExternalId()
    {
        ExternalIds.Add(new ExternalIdEditor());
    }

    [RelayCommand]
    private void RemoveExternalId(ExternalIdEditor? row)
    {
        if (row is not null) ExternalIds.Remove(row);
    }
}