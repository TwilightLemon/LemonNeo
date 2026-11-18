using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Library;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.ViewModels.Tools;

public sealed record LibraryStatistic(string Label, string Value);

public sealed class LibraryOverviewViewModel(
    Func<MusicLibrary> getLibrary,
    IProviderRegistry providers,
    LocalizationService text,
    Func<Guid, bool, Task> setPaused) : ObservableObject
{
    private IReadOnlyList<LibraryStatistic> _statistics = [];
    private MusicLibrary? _rowLibrary;
    public IReadOnlyList<LibraryStatistic> Statistics
    {
        get => _statistics;
        private set => SetProperty(ref _statistics, value);
    }
    public ObservableCollection<PlaylistSyncRowViewModel> SyncedPlaylists { get; } = [];
    public bool HasSyncedPlaylists => SyncedPlaylists.Count > 0;
    public string SyncedPlaylistCount => SyncedPlaylists.Count.ToString("N0", text.Culture);

    public void Refresh()
    {
        var library = getLibrary();
        var entries = library.Playlists.SelectMany(playlist => playlist.Items).ToArray();
        var unique = entries.GroupBy(item => item.Track.Id)
            .Select(group => group.FirstOrDefault(item => item.Track.Duration is not null) ?? group.First()).ToArray();
        var durations = unique.Where(item => item.Track.Duration is not null).ToArray();
        LibraryStatistic[] statistics =
        [
            new(text[LanguageManager.Keys.LibraryPlaylistTotal], library.Playlists.Count.ToString("N0", text.Culture)),
            new(text[LanguageManager.Keys.LibraryUniqueTracks], unique.Length.ToString("N0", text.Culture)),
            new(text[LanguageManager.Keys.LibraryTrackEntries], entries.Length.ToString("N0", text.Culture)),
            new(text[LanguageManager.Keys.LibraryKnownDuration], durations.Length == 0 ? "—" :
                TrackRow.FormatTime(TimeSpan.FromSeconds(durations.Sum(item => item.Track.Duration!.Value.TotalSeconds))))
        ];
        if (!Statistics.SequenceEqual(statistics)) Statistics = statistics;

        var existing = ReferenceEquals(_rowLibrary, library)
            ? SyncedPlaylists.ToDictionary(row => row.PlaylistId) : [];
        _rowLibrary = library;
        var mirrors = library.Mirrors.ToDictionary(mirror => mirror.PlaylistId);
        var rows = library.Playlists.Where(playlist => mirrors.ContainsKey(playlist.Id))
            .OrderBy(playlist => playlist.Name, StringComparer.Create(text.Culture, true)).Select(playlist =>
            {
                if (!existing.TryGetValue(playlist.Id, out var row))
                    row = new PlaylistSyncRowViewModel(playlist.Id, text, async paused =>
                    {
                        // A cached row must never update a different library with a matching playlist ID.
                        if (ReferenceEquals(library, getLibrary())) await setPaused(playlist.Id, paused);
                    }, Refresh);
                var mirror = mirrors[playlist.Id];
                var source = mirror.SourceKind == PlaylistSourceKind.Folder
                    ? text[LanguageManager.Keys.FolderSyncSource]
                    : providers.Get(mirror.ProviderId)?.Descriptor.Name ?? mirror.ProviderId;
                row.Update(playlist, mirror, source, library.IsPlaylistSyncPaused);
                return row;
            }).ToArray();
        CollectionSynchronizer.Update(SyncedPlaylists, rows);
        OnPropertyChanged(nameof(HasSyncedPlaylists));
        OnPropertyChanged(nameof(SyncedPlaylistCount));
    }
}

public sealed class PlaylistSyncRowViewModel(
    Guid playlistId,
    LocalizationService text,
    Func<bool, Task> setPaused,
    Action refresh) : ObservableObject
{
    private PlaylistMirror _mirror = null!;
    private bool _globalPaused;
    private bool _isSaving;
    public Guid PlaylistId { get; } = playlistId;
    public string Name { get; private set; } = "";
    public string Summary { get; private set; } = "";
    public string Details { get; private set; } = "";
    public bool HasError => _mirror.LastError is not null;
    public string Status => text[_globalPaused ? "PlaylistSyncGlobalPaused" :
        _mirror.IsPaused ? "PlaylistSyncPaused" : HasError ? "PlaylistSyncFailed" : "PlaylistSyncEnabled"];
    public bool CanChangeAutoSync => !_isSaving;

    public bool IsAutoSyncEnabled
    {
        get => !_mirror.IsPaused;
        set
        {
            if (value != IsAutoSyncEnabled && CanChangeAutoSync) _ = SetAutoSyncEnabledAsync(value);
        }
    }

    public async Task SetAutoSyncEnabledAsync(bool enabled)
    {
        if (!CanChangeAutoSync || enabled == IsAutoSyncEnabled) return;
        _isSaving = true;
        OnPropertyChanged(nameof(CanChangeAutoSync));
        try { await setPaused(!enabled); }
        finally
        {
            _isSaving = false;
            refresh();
            OnPropertyChanged(nameof(CanChangeAutoSync));
            OnPropertyChanged(nameof(IsAutoSyncEnabled));
        }
    }

    public void Update(Playlist playlist, PlaylistMirror mirror, string source, bool globalPaused)
    {
        _mirror = mirror;
        _globalPaused = globalPaused;
        Name = playlist.Name;
        Summary = $"{source} · {text.Format(LanguageManager.Keys.TrackCount, playlist.Items.Count)}";
        Details = text.Format(LanguageManager.Keys.PlaylistSyncStatus,
            mirror.DirectoryPath ?? source, Status,
            mirror.LastSynchronizedAt?.ToLocalTime().ToString("g", text.Culture) ?? "—") +
            (mirror.LastError is { } error ? Environment.NewLine + error : "");
        foreach (var name in new[]
                 {
                     nameof(Name), nameof(Summary), nameof(Details), nameof(Status), nameof(HasError),
                     nameof(IsAutoSyncEnabled)
                 }) OnPropertyChanged(name);
    }
}
