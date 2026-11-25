using CommunityToolkit.Mvvm.Input;
using LemonNeo.Application.Library;
using LemonNeo.Application.Playback;
using LemonNeo.Core.Library;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Playback;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.ViewModels.Library;

public sealed class LibraryViewModel : TrackListViewModel
{
    private readonly Guid? _playlistId;
    private Track? _lastPlaybackTrack;
    private Guid[] _queueIds = [];

    public LibraryViewModel(DesktopContext context, DesktopNavigation navigation, PlaybackViewModel playback,
        string route, Guid? playlistId = null)
        : base(context, navigation, playback, route)
    {
        _playlistId = playlistId;
        RemoveTrackCommand = new AsyncRelayCommand(RemoveSelectedTrackAsync);
        MoveTrackUpCommand = new AsyncRelayCommand(() => MoveSelectedTrackAsync(-1));
        MoveTrackDownCommand = new AsyncRelayCommand(() => MoveSelectedTrackAsync(1));
        NewTrackCommand = new RelayCommand(() =>
        {
            if (!CanEditPlaylist) return;
            _ = Navigation.OpenEditorAsync(new TrackRow(
                new Track { Id = new MusicItemId("library", Guid.NewGuid().ToString("N")), Title = "" }, 0, _text,
                PlaylistId: SelectedPlaylist!.Id), true);
        });
        MatchPlaylistCommand = new AsyncRelayCommand(() => CanMatchPlaylist && SelectedPlaylist is { } playlist
            ? Navigation.NavigateAsync(DesktopRoutes.MatchPlaylist, playlist.Id)
            : Task.CompletedTask);
        RefreshCommand = new AsyncRelayCommand(ReloadLibraryAsync);
        SyncPlaylistCommand = new AsyncRelayCommand(() => _playlistId is { } id
            ? Synchronization.SynchronizeAsync(id) : Task.CompletedTask);
        ToggleSyncPauseCommand = new AsyncRelayCommand(() => _playlistId is { } id && Mirror is { } mirror
            ? Synchronization.SetPausedAsync(id, !mirror.IsPaused) : Task.CompletedTask);
        DetachPlaylistCommand = new AsyncRelayCommand(() => ExecuteAsync(async ct =>
        {
            if (_playlistId is not { } id) return;
            await _host.Get<PlaylistSyncService>().DetachAsync(id, ct);
            Context.RefreshLibrary();
        }));
        CopyPlaylistCommand = new AsyncRelayCommand(() => ExecuteAsync(async ct =>
        {
            if (_playlistId is not { } id) return;
            var copy = _host.Get<PlaylistService>().CreateEditableCopy(id);
            _host.Get<PlaylistService>().Update(copy.Id,
                _text.Format(LanguageManager.Keys.EditablePlaylistCopyName, copy.Name), copy.Description);
            await _host.Get<LibraryService>().SaveAsync(ct);
            Context.RefreshLibrary();
            await Navigation.NavigateAsync(DesktopRoutes.Library, copy.Id);
        }));
        Playback.MetadataChanged += OnPlaybackChanged;
        Playback.QueueChanged += OnPlaybackChanged;
        RefreshTracks();
    }

    public Playlist? SelectedPlaylist => Playlists.FirstOrDefault(p => p.Id == _playlistId);
    public bool IsLibrary => Route == DesktopRoutes.Library;
    public bool IsQueue => Route == DesktopRoutes.Queue;
    public PlaylistSynchronizationViewModel Synchronization => Context.Synchronization;
    public PlaylistMirror? Mirror => _playlistId is { } id ? _host.Get<PlaylistSyncService>().GetMirror(id) : null;
    public bool IsMirrored => Mirror is not null;
    public string SyncPauseLabel => _text[Mirror?.IsPaused == true ? "ResumePlaylistSync" : "PausePlaylistSync"];
    public string? SyncDescription => Mirror switch
    {
        { LastSynchronizedAt: { } syncedAt } =>
            _text.Format(LanguageManager.Keys.PlaylistLastSynced, FormatSyncAge(syncedAt)),
        not null => _text[LanguageManager.Keys.PlaylistNeverSynced],
        _ => null
    };

    public string? SyncDetails => Mirror is { } mirror
        ? _text.Format(LanguageManager.Keys.PlaylistSyncStatus,
            mirror.SourceKind == PlaylistSourceKind.Folder ? mirror.DirectoryPath : mirror.ProviderId,
            _text[mirror.IsPaused || Synchronization.IsPaused ? "PlaylistSyncPaused" : "PlaylistSyncAutomatic"],
            mirror.LastSynchronizedAt is { } syncedAt ? FormatSyncAge(syncedAt) : "—") +
          (mirror.LastError is { } error ? Environment.NewLine + error : "")
        : null;
    public bool CanEditPlaylist => CanInteract && IsLibrary && SelectedPlaylist is not null && !IsMirrored;
    public bool CanEditPlaylistArtwork => CanInteract && HasSelectedPlaylist && !_host.Get<PlaylistService>()
        .IsArtworkReadOnly(SelectedPlaylist!.Id);
    public ArtworkService ArtworkLoader => _host.Get<ArtworkService>();
    public Uri? PlaylistArtworkUri => PlaylistArtworkFiles.Resolve(_host.StorageLocations.LibraryDirectory,
        SelectedPlaylist?.ArtworkUri);
    public bool CanMatchPlaylist => CanInteract && HasSelectedPlaylist && SelectedPlaylist!.Items.Count > 0;
    public bool HasSelectedPlaylist => IsLibrary && SelectedPlaylist is not null;
    protected override bool HasPlayableTracks => HasSelectedPlaylist ? UnfilteredTracks.Count > 0 : base.HasPlayableTracks;

    public bool CanEditTrack => CanInspectTrack && SelectedTrack!.PlaylistId is not null &&
                                SelectedTrack.PlaylistItemId is not null &&
                                !_host.Get<PlaylistService>().IsReadOnly(SelectedTrack.PlaylistId.Value);

    public bool CanRemoveTracks => CanEditPlaylist && HasActionTracks;
    public bool CanReorderTracks => CanEditPlaylist && IsSelectionMode && string.IsNullOrWhiteSpace(Filter);
    public bool CanMoveUp => CanEditTrack && SelectedTrack!.Number > 1;
    public bool CanMoveDown => CanEditTrack && SelectedTrack!.Number < SelectedPlaylist?.Items.Count;
    public override string PageTitle => SelectedPlaylist?.Name ?? base.PageTitle;
    public override string PageDescription => IsLibrary ? SelectedPlaylist?.Description ?? "" : base.PageDescription;

    public string EmptyTitle =>
        _text[
            !string.IsNullOrWhiteSpace(Filter) ? "NoResults" :
            IsQueue ? "EmptyQueue" :
            SelectedPlaylist is null ? "EmptyLibrary" : "PlaylistEmpty"];

    public string EmptyDescription =>
        _text[
            !string.IsNullOrWhiteSpace(Filter) ? "NoResultsDescription" :
            IsQueue ? "EmptyQueueDescription" :
            SelectedPlaylist is null ? "EmptyLibraryDescription" : "PlaylistEmptyDescription"];

    public AsyncRelayCommand RemoveTrackCommand { get; }
    public AsyncRelayCommand MoveTrackUpCommand { get; }
    public AsyncRelayCommand MoveTrackDownCommand { get; }
    public RelayCommand NewTrackCommand { get; }
    public AsyncRelayCommand MatchPlaylistCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SyncPlaylistCommand { get; }
    public AsyncRelayCommand ToggleSyncPauseCommand { get; }
    public AsyncRelayCommand DetachPlaylistCommand { get; }
    public AsyncRelayCommand CopyPlaylistCommand { get; }

    protected override IEnumerable<TrackRow> GetTracks()
    {
        return IsQueue
            ? _host.Get<PlaybackSessionService>().Queue.Items
                .Select((i, index) => new TrackRow(i.Track, index + 1, _text, i.Id, playbackEntry: i.PlaylistEntry))
            : SelectedPlaylist is { } playlist
                ? playlist.Items.OrderBy(i => FractionalPlaylistOrderingService.Parse(i.SortKey)).ThenBy(i => i.Id)
                    .Select((i, index) =>
                        new TrackRow(i.Track, index + 1, _text, PlaylistId: playlist.Id, PlaylistItemId: i.Id))
                : Playlists.SelectMany(p => p.Items).Select(i => i.Track).DistinctBy(t => t.Id).OrderBy(t => t.Title)
                    .Select((t, index) => new TrackRow(t, index + 1, _text));
    }

    public override Task PlayAsync(TrackRow? selected = null)
    {
        return Playback.ExecutePlaybackAsync(async ct =>
        {
            var session = _host.Get<PlaybackSessionService>();
            if (IsQueue)
            {
                if ((selected ?? Tracks.FirstOrDefault())?.QueueItemId is { } id)
                    await session.PlayQueueItemAsync(id, ct);
                return;
            }

            var rows = HasSelectedPlaylist ? UnfilteredTracks : Tracks;
            var index = selected is null ? 0 : rows.IndexOf(selected);
            if (rows.Count == 0 || index < 0) return;
            var tracks = rows.Select(row => new PlaybackQueueItem
            {
                Track = row.Track,
                PlaylistEntry = row.PlaybackEntry
            }).ToArray();
            await session.PlayItemsAsync(tracks, index, cancellationToken: ct);
        });
    }

    public Task ReloadLibraryAsync()
    {
        return ExecuteAsync(async ct =>
        {
            await _host.Get<LibraryService>().LoadAsync(ct);
            Context.RefreshLibrary();
        });
    }

    public Task CreatePlaylistAsync(string name, string? description = null, string? folderPath = null,
        bool synchronize = false)
    {
        return ExecuteAsync(async ct =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var playlists = _host.Get<PlaylistService>();
            Playlist playlist;
            FolderImportResult? import = null;
            if (folderPath is null)
            {
                playlist = playlists.Create(name, description);
            }
            else
            {
                var importer = _host.Get<FolderImportService>();
                var scan = await Task.Run(() => importer.ScanAsync(folderPath, cancellationToken: ct), ct);
                ct.ThrowIfCancellationRequested();
                if (synchronize)
                {
                    playlist = _host.Get<PlaylistSyncService>().ImportFolder(scan, name, description);
                    import = new FolderImportResult(playlist, scan.Tracks.Count, scan.SkippedFiles);
                }
                else
                {
                    import = importer.ImportScan(scan, name);
                    playlist = playlists.Update(import.Playlist.Id, name, description);
                }
            }

            await _host.Get<LibraryService>().SaveAsync(ct);
            Context.RefreshLibrary();
            if (ReferenceEquals(Navigation.Current, this))
                await Navigation.NavigateAsync(DesktopRoutes.Library, playlist.Id);
            if (import is null) Notify(NotificationLevel.Success, LanguageManager.Keys.PlaylistCreated, playlist.Name);
            else Notify(import.SkippedFiles.Count > 0 ? NotificationLevel.Warning : NotificationLevel.Success,
                LanguageManager.Keys.Imported, import.ImportedTracks, import.SkippedFiles.Count);
        });
    }

    public Task UpdatePlaylistAsync(Playlist playlist, string name, string? description,
        bool artworkChanged, Uri? artworkFile)
    {
        return ExecuteAsync(async ct =>
        {
            var playlists = _host.Get<PlaylistService>();
            var library = _host.Get<LibraryService>();
            using var lease = await _host.Get<PlaylistSyncService>().AcquireLibraryChangeAsync(ct);
            if (!ReferenceEquals(playlists.GetRequired(playlist.Id), playlist) || playlists.IsArtworkReadOnly(playlist.Id))
                return;

            Uri? artwork = null;
            if (artworkChanged && artworkFile is not null)
            {
                using var image = await ArtworkLoader.LoadWithDataAsync(artworkFile, ct)
                    ?? throw new InvalidOperationException(_text[LanguageManager.Keys.PlaylistCoverUnavailable]);
                artwork = await PlaylistArtworkFiles.SaveAsync(_host.StorageLocations.LibraryDirectory,
                    image.EncodedData, ct);
            }

            ct.ThrowIfCancellationRequested();
            if (!playlists.IsReadOnly(playlist.Id)) playlists.Update(playlist.Id, name, description);
            if (artworkChanged) playlists.UpdateArtwork(playlist.Id, artwork);
            await library.SaveAsync(CancellationToken.None);
            Context.RefreshLibrary();
            Notify(NotificationLevel.Success, LanguageManager.Keys.LibrarySaved);
        });
    }

    public Task DeletePlaylistAsync(Guid id)
    {
        return ExecuteAsync(async ct =>
        {
            _host.Get<PlaylistService>().Delete(id);
            await _host.Get<LibraryService>().SaveAsync(ct);
            if (ReferenceEquals(Navigation.Current, this) && SelectedPlaylist?.Id == id)
                await Navigation.NavigateAsync(DesktopRoutes.Library);
            Context.RefreshLibrary();
            Notify(NotificationLevel.Success, LanguageManager.Keys.LibrarySaved);
        });
    }

    public string GetDeletePlaylistPrompt(string name)
    {
        return _text.Format(LanguageManager.Keys.DeletePlaylistPrompt, name);
    }

    public Task RemoveSelectedTrackAsync()
    {
        if (!CanRemoveTracks || SelectedPlaylist is not { } playlist) return Task.CompletedTask;
        var ids = ActionTracks.Select(t => t.PlaylistItemId).OfType<Guid>().ToArray();
        return RemovePlaylistTracksAsync(playlist.Id, ids);
    }

    internal bool CanRemoveTrack(TrackRow row) => CanInteract &&
        (IsQueue && row.QueueItemId is not null ||
         CanEditPlaylist && row.PlaylistItemId is not null && row.PlaylistId == SelectedPlaylist?.Id);

    internal Task RemoveTrackAsync(TrackRow row)
    {
        if (!CanRemoveTrack(row) || !Tracks.Contains(row)) return Task.CompletedTask;
        if (IsQueue && row.QueueItemId is { } queueId)
            return Playback.ExecutePlaybackAsync(ct => _host.Get<PlaybackSessionService>().RemoveQueueItemAsync(queueId, ct));
        return RemovePlaylistTracksAsync(row.PlaylistId!.Value, [row.PlaylistItemId!.Value]);
    }

    private Task RemovePlaylistTracksAsync(Guid playlistId, IReadOnlyList<Guid> ids)
    {
        return ExecuteAsync(async ct =>
        {
            var playlists = _host.Get<PlaylistService>();
            foreach (var id in ids) playlists.Remove(playlistId, id);
            await _host.Get<LibraryService>().SaveAsync(ct);
            Context.RefreshLibrary();
            Notify(NotificationLevel.Success, LanguageManager.Keys.LibrarySaved);
        });
    }

    public Task ReorderTracksAsync(TrackRow source, TrackRow target, bool after)
    {
        if (!CanReorderTracks || SelectedPlaylist is not { } playlist || source.PlaylistItemId is not { } sourceId ||
            target.PlaylistItemId is not { } targetId)
            return Task.CompletedTask;
        var ids = IsSelectionMode && source.IsMarked
            ? ActionTracks.Select(t => t.PlaylistItemId!.Value).ToArray()
            : [sourceId];
        return ExecuteAsync(async ct =>
        {
            _host.Get<PlaylistService>().Reorder(playlist.Id, ids, targetId, after);
            await _host.Get<LibraryService>().SaveAsync(ct);
            Context.RefreshLibrary();
            Notify(NotificationLevel.Success, LanguageManager.Keys.LibrarySaved);
        });
    }

    public Task MoveSelectedTrackAsync(int offset)
    {
        if (!CanEditTrack || SelectedTrack is not
                { PlaylistId: { } playlistId, PlaylistItemId: { } itemId, Number: var number })
            return Task.CompletedTask;
        return MoveTrackAsync(playlistId, itemId, number + offset);
    }

    public Task MoveTrackAsync(Guid playlistId, Guid itemId, int position)
    {
        return ExecuteAsync(async ct =>
        {
            _host.Get<PlaylistService>().Move(playlistId, itemId, position);
            await _host.Get<LibraryService>().SaveAsync(ct);
            Context.RefreshLibrary();
            Notify(NotificationLevel.Success, LanguageManager.Keys.LibrarySaved);
        });
    }

    internal void RefreshSyncTime()
    {
        if (Mirror?.LastSynchronizedAt is null) return;
        OnPropertyChanged(nameof(SyncDescription));
        OnPropertyChanged(nameof(SyncDetails));
    }

    private string FormatSyncAge(DateTimeOffset syncedAt)
    {
        var elapsed = DateTimeOffset.UtcNow - syncedAt;
        if (elapsed.TotalMinutes < 1) return _text[LanguageManager.Keys.RelativeTimeJustNow];

        var (count, singular, plural) = elapsed switch
        {
            { Days: > 0 } => (elapsed.Days, LanguageManager.Keys.RelativeTimeDayAgo, LanguageManager.Keys.RelativeTimeDaysAgo),
            { Hours: > 0 } => (elapsed.Hours, LanguageManager.Keys.RelativeTimeHourAgo, LanguageManager.Keys.RelativeTimeHoursAgo),
            _ => (elapsed.Minutes, LanguageManager.Keys.RelativeTimeMinuteAgo, LanguageManager.Keys.RelativeTimeMinutesAgo)
        };
        return count == 1 ? _text[singular] : _text.Format(plural, count);
    }

    private void OnPlaybackChanged(object? sender, EventArgs args)
    {
        if (!IsNavigationActive)
        {
            if (!ReferenceEquals(_lastPlaybackTrack, Playback.Snapshot.CurrentTrack))
            {
                _lastPlaybackTrack = Playback.Snapshot.CurrentTrack;
                MarkTracksDirty();
            }
            if (IsQueue) MarkTracksDirty();
            return;
        }
        // Resolution can enrich the stored snapshots with the chosen source ID.
        // Refresh only when metadata changes, not on every playback timeline tick.
        if (!ReferenceEquals(_lastPlaybackTrack, Playback.Snapshot.CurrentTrack))
        {
            _lastPlaybackTrack = Playback.Snapshot.CurrentTrack;
            var latest = GetTracks()
                .ToDictionary(row => row.PlaylistItemId?.ToString() ?? row.QueueItemId?.ToString() ?? row.Identity,
                    row => row.Track);
            if (Tracks.Any(row =>
                    latest.TryGetValue(row.PlaylistItemId?.ToString() ?? row.QueueItemId?.ToString() ?? row.Identity,
                        out var track) && !ReferenceEquals(row.Track, track)))
                RefreshTracks();
        }

        if (!IsQueue) return;
        var ids = _host.Get<PlaybackSessionService>().Queue.Items.Select(i => i.Id).ToArray();
        if (_queueIds.SequenceEqual(ids)) return;
        _queueIds = ids;
        RefreshTracks();
    }

    protected override void RaiseInteractionProperties()
    {
        base.RaiseInteractionProperties();
        foreach (var name in new[]
                 {
                     nameof(CanEditPlaylist), nameof(CanEditTrack), nameof(CanMoveUp), nameof(CanMoveDown),
                     nameof(HasSelectedPlaylist), nameof(CanEditPlaylistArtwork), nameof(PlaylistArtworkUri),
                     nameof(SelectedPlaylist), nameof(EmptyTitle), nameof(EmptyDescription), nameof(CanRemoveTracks),
                     nameof(CanReorderTracks), nameof(IsMirrored), nameof(SyncPauseLabel), nameof(SyncDescription),
                     nameof(SyncDetails), nameof(CanMatchPlaylist)
                 }) OnPropertyChanged(name);
    }

    public override async ValueTask DisposeAsync()
    {
        Playback.MetadataChanged -= OnPlaybackChanged;
        Playback.QueueChanged -= OnPlaybackChanged;
        await base.DisposeAsync();
    }
}
