using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Desktop.ViewModels;

public sealed partial class LibraryViewModel : TrackListViewModel
{
    private readonly Guid? _playlistId;
    private Guid[] _queueIds = [];
    [ObservableProperty] private string _metadataTargets = "";
    [ObservableProperty] private string _inspectionMessages = "";
    public LibraryViewModel(DesktopContext context, DesktopNavigation navigation, PlaybackViewModel playback, string route, Guid? playlistId = null)
        : base(context, navigation, playback, route)
    {
        _playlistId = playlistId;
        RemoveTrackCommand = new(RemoveSelectedTrackAsync);
        MoveTrackUpCommand = new(() => MoveSelectedTrackAsync(-1));
        MoveTrackDownCommand = new(() => MoveSelectedTrackAsync(1));
        NewTrackCommand = new(() =>
        {
            if (!CanEditPlaylist) return;
            _ = Navigation.OpenEditorAsync(new TrackRow(new Track { Id = new("library", Guid.NewGuid().ToString("N")), Title = "" }, 0, _text,
                PlaylistId: SelectedPlaylist!.Id), true);
        });
        MatchPlaylistCommand = new(() => SelectedPlaylist is { } playlist ? MatchPlaylistAsync(playlist.Id) : Task.CompletedTask);
        RefreshCommand = new(ReloadLibraryAsync);
        Playback.Changed += OnPlaybackChanged;
        RefreshTracks();
    }
    public Playlist? SelectedPlaylist => Playlists.FirstOrDefault(p => p.Id == _playlistId);
    public bool IsLibrary => Route == DesktopRoutes.Library;
    public bool IsQueue => Route == DesktopRoutes.Queue;
    public bool CanEditPlaylist => CanInteract && IsLibrary && SelectedPlaylist is not null;
    public bool HasSelectedPlaylist => IsLibrary && SelectedPlaylist is not null;
    public bool CanEditTrack => CanInspectTrack && SelectedTrack!.PlaylistId is not null && SelectedTrack.PlaylistItemId is not null;
    public bool CanMoveUp => CanEditTrack && SelectedTrack!.Number > 1;
    public bool CanMoveDown => CanEditTrack && SelectedTrack!.Number < SelectedPlaylist?.Items.Count;
    public override string PageTitle => SelectedPlaylist?.Name ?? base.PageTitle;
    public override string PageDescription => IsLibrary ? SelectedPlaylist?.Description ?? "" : base.PageDescription;
    public string EmptyTitle => _text[!string.IsNullOrWhiteSpace(Filter) ? "NoResults" : IsQueue ? "EmptyQueue" : SelectedPlaylist is null ? "EmptyLibrary" : "PlaylistEmpty"];
    public string EmptyDescription => _text[!string.IsNullOrWhiteSpace(Filter) ? "NoResultsDescription" : IsQueue ? "EmptyQueueDescription" : SelectedPlaylist is null ? "EmptyLibraryDescription" : "PlaylistEmptyDescription"];
    public AsyncRelayCommand ClearQueueCommand => Playback.ClearQueueCommand;
    public AsyncRelayCommand RemoveTrackCommand { get; }
    public AsyncRelayCommand MoveTrackUpCommand { get; }
    public AsyncRelayCommand MoveTrackDownCommand { get; }
    public RelayCommand NewTrackCommand { get; }
    public AsyncRelayCommand MatchPlaylistCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public string MetadataNamespaces => string.Join(", ", _runtime.Get<IProviderRegistry>().Providers.OfType<IMetadataProvider>()
        .SelectMany(p => p.MetadataNamespaces).Distinct(StringComparer.OrdinalIgnoreCase).Order());
    protected override IEnumerable<TrackRow> GetTracks() => IsQueue
        ? _runtime.Get<PlaybackSessionService>().Queue.Items.Select((i, index) => new TrackRow(i.Track, index + 1, _text, QueueItemId: i.Id))
        : SelectedPlaylist is { } playlist
            ? playlist.Items.OrderBy(i => FractionalPlaylistOrderingService.Parse(i.SortKey)).ThenBy(i => i.Id)
                .Select((i, index) => new TrackRow(i.Track, index + 1, _text, PlaylistId: playlist.Id, PlaylistItemId: i.Id))
            : Playlists.SelectMany(p => p.Items).Select(i => i.Track).DistinctBy(t => t.Id).OrderBy(t => t.Title)
                .Select((t, index) => new TrackRow(t, index + 1, _text));
    public override Task PlayAsync(TrackRow? selected = null) => Playback.ExecutePlaybackAsync(async ct =>
    {
        var session = _runtime.Get<PlaybackSessionService>();
        if (IsQueue)
        {
            if ((selected ?? Tracks.FirstOrDefault())?.QueueItemId is { } id) await session.PlayQueueItemAsync(id, ct);
            return;
        }
        var tracks = Tracks.Select(t => t.Track).ToArray();
        var index = selected is null ? 0 : Tracks.IndexOf(selected);
        if (tracks.Length > 0 && index >= 0) await session.PlayPlaylistAsync(tracks, index, cancellationToken: ct);
    });
    public Task ReloadLibraryAsync() => ExecuteAsync(async ct =>
    {
        await _runtime.Get<LibraryService>().LoadAsync(ct); Context.RefreshLibrary();
    });
    public Task CreatePlaylistAsync(string name, string? description = null, string? folderPath = null) => ExecuteAsync(async ct =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var playlists = _runtime.Get<PlaylistService>();
        Playlist playlist;
        FolderImportResult? import = null;
        if (folderPath is null) playlist = playlists.Create(name, description);
        else
        {
            var importer = _runtime.Get<FolderImportService>();
            var scan = await Task.Run(() => importer.ScanAsync(folderPath, cancellationToken: ct), ct);
            ct.ThrowIfCancellationRequested();
            import = importer.ImportScan(scan, name);
            playlist = playlists.Update(import.Playlist.Id, name, description);
        }
        await _runtime.Get<LibraryService>().SaveAsync(ct);
        Context.RefreshLibrary();
        if (ReferenceEquals(Navigation.Current, this)) await Navigation.NavigateAsync(DesktopRoutes.Library, playlist.Id);
        if (import is null) SetStatus("PlaylistCreated", playlist.Name);
        else SetStatus("Imported", import.ImportedTracks, import.SkippedFiles.Count);
    });

    public Task UpdatePlaylistAsync(Guid id, string name, string? description) => ExecuteAsync(async ct =>
    {
        _runtime.Get<PlaylistService>().Update(id, name, description);
        await _runtime.Get<LibraryService>().SaveAsync(ct);
        Context.RefreshLibrary();
        SetStatus("LibrarySaved");
    });

    public Task DeletePlaylistAsync(Guid id) => ExecuteAsync(async ct =>
    {
        _runtime.Get<PlaylistService>().Delete(id);
        await _runtime.Get<LibraryService>().SaveAsync(ct);
        if (ReferenceEquals(Navigation.Current, this) && SelectedPlaylist?.Id == id) await Navigation.NavigateAsync(DesktopRoutes.Library);
        Context.RefreshLibrary();
        SetStatus("LibrarySaved");
    });

    public string GetDeletePlaylistPrompt(string name) => _text.Format("DeletePlaylistPrompt", name);

    public Task RemoveSelectedTrackAsync()
    {
        if (!CanEditTrack || SelectedTrack is not { PlaylistId: { } playlistId, PlaylistItemId: { } itemId }) return Task.CompletedTask;
        return ExecuteAsync(async ct =>
        {
            _runtime.Get<PlaylistService>().Remove(playlistId, itemId);
            await _runtime.Get<LibraryService>().SaveAsync(ct);
            Context.RefreshLibrary();
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
        Context.RefreshLibrary();
        SetStatus("LibrarySaved");
    });

    public Task MatchPlaylistAsync(Guid playlistId) => ExecuteAsync(async ct =>
    {
        var playlist = _runtime.Get<PlaylistService>().GetRequired(playlistId);
        var targets = ParseNamespaces(MetadataTargets);
        var tracks = playlist.Items.Select(i => i.Track).DistinctBy(t => t.Id).ToArray();
        using var concurrency = new SemaphoreSlim(3);
        var results = await Task.WhenAll(tracks.Select(async track =>
        {
            await concurrency.WaitAsync(ct);
            try { return (Original: track, Result: await Task.Run(() => _runtime.Get<MetadataService>().EnrichAsync(track, targets, ct), ct)); }
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
        Context.RefreshLibrary();
        SetStatus("PlaylistMatched", matched, tracks.Length);
    });

    private static string[] ParseNamespaces(string text) => text.Split([',', ';', ' ', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static string FormatErrors(IEnumerable<KeyValuePair<string, string>> errors) => string.Join(Environment.NewLine, errors.Select(e => $"{e.Key}: {e.Value}"));
    private void OnPlaybackChanged(object? sender, EventArgs args)
    {
        if (!IsQueue) return;
        var ids = _runtime.Get<PlaybackSessionService>().Queue.Items.Select(i => i.Id).ToArray();
        if (_queueIds.SequenceEqual(ids)) return;
        _queueIds = ids;
        RefreshTracks();
    }
    protected override void RaiseInteractionProperties()
    {
        base.RaiseInteractionProperties();
        foreach (var name in new[] { nameof(CanEditPlaylist), nameof(CanEditTrack), nameof(CanMoveUp), nameof(CanMoveDown), nameof(HasSelectedPlaylist),
            nameof(SelectedPlaylist), nameof(EmptyTitle), nameof(EmptyDescription) }) OnPropertyChanged(name);
    }
    public override async ValueTask DisposeAsync() { Playback.Changed -= OnPlaybackChanged; await base.DisposeAsync(); }
}
