using CommunityToolkit.Mvvm.Input;
using LemonNeo.Application;
using LemonNeo.Core;

namespace LemonNeo.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private TrackEditorViewModel? _editor;
    private Playlist? _targetPlaylist;
    private DesktopPage _editorReturnPage;
    private Guid? _editorReturnPlaylist;
    public TrackEditorViewModel? Editor { get => _editor; private set { SetProperty(ref _editor, value); RaiseSelectionProperties(); } }
    public Playlist? TargetPlaylist { get => _targetPlaylist; set { if (SetProperty(ref _targetPlaylist, value)) RaiseSelectionProperties(); } }
    public bool CanEditPlaylist => CanInteract && IsLibrary && SelectedPlaylist is not null;
    public bool HasSelectedPlaylist => IsLibrary && SelectedPlaylist is not null;
    public bool CanInspectTrack => CanInteract && SelectedTrack is not null;
    public bool CanEditTrack => CanInspectTrack && SelectedTrack!.PlaylistId is not null && SelectedTrack.PlaylistItemId is not null;
    public bool CanMoveUp => CanEditTrack && SelectedTrack!.Number > 1;
    public bool CanMoveDown => CanEditTrack && SelectedTrack!.Number < SelectedPlaylist?.Items.Count;
    public bool CanAddToPlaylist => CanInspectTrack && TargetPlaylist is not null;
    public bool CanSaveEditor => CanInteract && Editor is { } editor && (editor.PlaylistId is not null || TargetPlaylist is not null);
    public bool EditorNeedsPlaylist => Editor is { PlaylistId: null };
    public AsyncRelayCommand RemoveTrackCommand { get; private set; } = null!;
    public AsyncRelayCommand MoveTrackUpCommand { get; private set; } = null!;
    public AsyncRelayCommand MoveTrackDownCommand { get; private set; } = null!;
    public RelayCommand InspectTrackCommand { get; private set; } = null!;
    public RelayCommand NewTrackCommand { get; private set; } = null!;
    public RelayCommand CloseEditorCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveEditorCommand { get; private set; } = null!;
    public AsyncRelayCommand AddToPlaylistCommand { get; private set; } = null!;
    public AsyncRelayCommand MatchPlaylistCommand { get; private set; } = null!;

    private void InitializeLibraryCommands()
    {
        RemoveTrackCommand = new(() => RemoveSelectedTrackAsync());
        MoveTrackUpCommand = new(() => MoveSelectedTrackAsync(-1));
        MoveTrackDownCommand = new(() => MoveSelectedTrackAsync(1));
        InspectTrackCommand = new(() => { if (SelectedTrack is { } row) OpenEditor(row); });
        NewTrackCommand = new(() =>
        {
            if (!CanEditPlaylist) return;
            OpenEditor(new TrackRow(new Track { Id = new("library", Guid.NewGuid().ToString("N")), Title = "" }, 0, _text,
                PlaylistId: SelectedPlaylist!.Id), isNew: true);
        });
        CloseEditorCommand = new(CloseEditor);
        SaveEditorCommand = new(SaveEditorAsync);
        AddToPlaylistCommand = new(() => SelectedTrack is { } row && TargetPlaylist is { } target
            ? AddTrackAsync(target.Id, row.Track) : Task.CompletedTask);
        MatchPlaylistCommand = new(() => SelectedPlaylist is { } playlist ? MatchPlaylistAsync(playlist.Id) : Task.CompletedTask);
    }

    public Task UpdatePlaylistAsync(Guid id, string name, string? description) => ExecuteAsync(async ct =>
    {
        _runtime.Get<PlaylistService>().Update(id, name, description);
        await _runtime.Get<LibraryService>().SaveAsync(ct);
        RefreshLibrary(id);
        SetStatus("LibrarySaved");
    });

    public Task DeletePlaylistAsync(Guid id) => ExecuteAsync(async ct =>
    {
        _runtime.Get<PlaylistService>().Delete(id);
        await _runtime.Get<LibraryService>().SaveAsync(ct);
        if (SelectedPlaylist?.Id == id) SelectedPlaylist = null;
        RefreshLibrary();
        SetStatus("LibrarySaved");
    });

    public string GetDeletePlaylistPrompt(string name) => _text.Format("DeletePlaylistPrompt", name);

    public Task AddTrackAsync(Guid playlistId, Track track) => ExecuteAsync(async ct =>
    {
        _runtime.Get<PlaylistService>().Add(playlistId, track);
        await _runtime.Get<LibraryService>().SaveAsync(ct);
        RefreshLibrary();
        SetStatus("TrackAdded");
    });

    public Task RemoveSelectedTrackAsync()
    {
        if (!CanEditTrack || SelectedTrack is not { PlaylistId: { } playlistId, PlaylistItemId: { } itemId }) return Task.CompletedTask;
        return ExecuteAsync(async ct =>
        {
            _runtime.Get<PlaylistService>().Remove(playlistId, itemId);
            await _runtime.Get<LibraryService>().SaveAsync(ct);
            RefreshLibrary(playlistId);
            SetStatus("LibrarySaved");
        });
    }

    public Task MoveSelectedTrackAsync(int offset)
    {
        if (!CanEditTrack || SelectedTrack is not { PlaylistId: { } playlistId, PlaylistItemId: { } itemId, Number: var number }) return Task.CompletedTask;
        return MoveTrackAsync(playlistId, itemId, number + offset);
    }

    public Task MoveTrackAsync(Guid playlistId, Guid itemId, int position) => ExecuteAsync(async ct =>
    {
        _runtime.Get<PlaylistService>().Move(playlistId, itemId, position);
        await _runtime.Get<LibraryService>().SaveAsync(ct);
        RefreshLibrary(playlistId);
        SetStatus("LibrarySaved");
    });

    public void OpenEditor(TrackRow row, bool isNew = false)
    {
        if (!CanInteract) return;
        _editorReturnPage = Page;
        _editorReturnPlaylist = SelectedPlaylist?.Id;
        Editor = new(row.Track, _text, row.PlaylistId, row.PlaylistItemId, isNew);
        ResetInspection();
        Page = DesktopPage.Editor;
        RaiseSelectionProperties();
    }

    private void CloseEditor()
    {
        if (IsBusy) return;
        Editor = null;
        Page = _editorReturnPage;
        if (Page == DesktopPage.Library)
            SelectedPlaylist = Playlists.FirstOrDefault(p => p.Id == _editorReturnPlaylist);
        RefreshTracks();
        RaiseSelectionProperties();
    }

    public Task SaveEditorAsync()
    {
        if (!CanSaveEditor || Editor is not { } editor) return Task.CompletedTask;
        var destination = editor.PlaylistId ?? TargetPlaylist!.Id;
        return ExecuteAsync(async ct =>
        {
            var track = editor.Build();
            var service = _runtime.Get<PlaylistService>();
            if (editor.ItemId is { } itemId) service.UpdateTrack(destination, itemId, track);
            else editor.AttachItem(destination, service.Add(destination, track).Id);
            await _runtime.Get<LibraryService>().SaveAsync(ct);
            // Stay in the editor on validation or persistence failure.
            Editor = null;
            Page = DesktopPage.Library;
            RefreshLibrary(destination);
            SetStatus("LibrarySaved");
        });
    }

    public Task MatchPlaylistAsync(Guid playlistId) => ExecuteAsync(async ct =>
    {
        var playlist = _runtime.Get<PlaylistService>().GetRequired(playlistId);
        var targets = ParseNamespaces(MetadataTargets);
        var tracks = playlist.Items.Select(i => i.Track).DistinctBy(t => t.Id).ToArray();
        using var concurrency = new SemaphoreSlim(3);
        var results = await Task.WhenAll(tracks.Select(async track =>
        {
            await concurrency.WaitAsync(ct);
            try { return (Original: track, Result: await _runtime.Get<MetadataService>().EnrichAsync(track, targets, ct)); }
            finally { concurrency.Release(); }
        }));
        ct.ThrowIfCancellationRequested();
        var matched = 0;
        InspectionMessages = FormatErrors(results.SelectMany(r => r.Result.Lookup.SourceErrors).DistinctBy(e => e.Key));
        foreach (var result in results.Where(r => !ReferenceEquals(r.Original, r.Result.Track)))
        {
            foreach (var item in playlist.Items.Where(i => i.Track.Id == result.Original.Id))
                _runtime.Get<PlaylistService>().UpdateTrack(playlistId, item.Id, result.Result.Track);
            matched++;
        }
        await _runtime.Get<LibraryService>().SaveAsync(ct);
        RefreshLibrary(playlistId);
        SetStatus("PlaylistMatched", matched, tracks.Length);
    });

    private void RaiseSelectionProperties()
    {
        foreach (var name in new[] { nameof(CanEditPlaylist), nameof(HasSelectedPlaylist), nameof(CanInspectTrack), nameof(CanEditTrack), nameof(CanMoveUp), nameof(CanMoveDown),
            nameof(CanAddToPlaylist), nameof(CanSaveEditor), nameof(EditorNeedsPlaylist), nameof(CanSearch), nameof(CanLogin),
            nameof(CanLogout), nameof(CanBrowseRemote), nameof(CanImportRemoteId), nameof(CanImportRemote) }) OnPropertyChanged(name);
        CancelOperationCommand?.NotifyCanExecuteChanged();
    }
}
