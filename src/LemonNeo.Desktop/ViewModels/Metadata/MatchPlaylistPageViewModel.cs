using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Application.Library;
using LemonNeo.Application.Providers;
using LemonNeo.Core.Library;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.ViewModels.Metadata;

public sealed partial class MatchPlaylistPageViewModel : PageViewModel
{
    private readonly DesktopNavigation _navigation;
    private readonly Guid _playlistId;
    [ObservableProperty] private string _inspectionMessages = "";

    public MatchPlaylistPageViewModel(DesktopContext context, DesktopNavigation navigation, Guid playlistId)
        : base(context, DesktopRoutes.MatchPlaylist)
    {
        _playlistId = playlistId;
        _navigation = navigation;
        CloseCommand = new AsyncRelayCommand(navigation.GoBackAsync);
        MatchPlaylistCommand = new AsyncRelayCommand(MatchPlaylistAsync, () => CanMatchPlaylist);
        OpenTrackCommand = new RelayCommand<PlaylistMatchResultRow>(row =>
        {
            if (CanInteract && row is not null) _ = OpenTrackAsync(row);
        }, row => CanInteract && row is not null);
        MetadataSources = MetadataSourceOption.Create(_host.Get<IProviderRegistry>(),
            _host.Get<IPlaybackSourceProviderRegistry>());
        foreach (var source in MetadataSources) source.PropertyChanged += OnMetadataSourceChanged;
        Context.LibraryChanged += OnLibraryChanged;
    }

    public Playlist? Playlist => Context.Playlists.FirstOrDefault(playlist => playlist.Id == _playlistId);

    public override string? PageSummary => Playlist is { } playlist
        ? $"{playlist.Name} · {_text.Format(LanguageManager.Keys.TrackSummary, playlist.Items.Count, TrackRow.FormatTime(TimeSpan.FromSeconds(playlist.Items.Sum(item => item.Track.Duration?.TotalSeconds ?? 0))))}"
        : null;

    public IReadOnlyList<MetadataSourceOption> MetadataSources { get; }
    public ObservableCollection<PlaylistMatchResultRow> Results { get; } = [];
    public bool HasMetadataSources => MetadataSources.Count > 0;
    public bool HasResults => Results.Count > 0;

    public bool CanMatchPlaylist => CanInteract && Playlist is { Items.Count: > 0 } &&
                                    MetadataSources.Any(source => source.IsSelected);

    public AsyncRelayCommand CloseCommand { get; }
    public AsyncRelayCommand MatchPlaylistCommand { get; }
    public RelayCommand<PlaylistMatchResultRow> OpenTrackCommand { get; }

    public Task MatchPlaylistAsync()
    {
        if (!CanMatchPlaylist) return Task.CompletedTask;
        // Capture the selection before starting provider work; empty never means "all" in this UI.
        var targets = MetadataSources.Where(source => source.IsSelected).Select(source => source.Namespace).ToArray();
        return ExecuteAsync(async ct =>
        {
            InspectionMessages = "";
            Results.Clear();
            OnPropertyChanged(nameof(HasResults));
            var playlists = _host.Get<PlaylistService>();
            var tracks = playlists.GetRequired(_playlistId).Items
                .Select((item, index) => (Item: item, Number: index + 1))
                .DistinctBy(entry => entry.Item.Track.Id)
                .ToArray();
            using var concurrency = new SemaphoreSlim(3);
            var matches = await Task.WhenAll(tracks.Select(async entry =>
            {
                await concurrency.WaitAsync(ct);
                try
                {
                    var result =
                        await Task.Run(
                            () => _host.Get<MetadataService>().MatchAndReplaceAsync(entry.Item.Track, targets, ct), ct);
                    return (entry.Number, entry.Item.Id, Original: entry.Item.Track, Result: result);
                }
                finally
                {
                    concurrency.Release();
                }
            }));
            ct.ThrowIfCancellationRequested();
            var results = matches.Select(match => new PlaylistMatchResultRow(
                match.Number, match.Id, match.Original, match.Result.Track, match.Result.Lookup, _text,
                OpenTrackCommand)).ToArray();
            InspectionMessages = string.Join(Environment.NewLine, results
                .SelectMany(result => result.Lookup.SourceErrors)
                .DistinctBy(error => error.Key).Select(error => $"{error.Key}: {error.Value}"));
            var playlist = playlists.GetRequired(_playlistId);
            var matched = 0;
            var changed = 0;
            foreach (var result in results.Where(result => result.IsSuccessful))
            {
                matched++;
                if (!result.HasChanges) continue;
                foreach (var item in playlist.Items.Where(item => item.Track.Id == result.Original.Id).ToArray())
                    playlists.UpdateTrack(_playlistId, item.Id, result.Updated);
                changed++;
            }

            await _host.Get<LibraryService>().SaveAsync(ct);
            Context.RefreshLibrary();
            foreach (var result in results.OrderBy(result => result.IsSuccessful).ThenBy(result => result.Number))
                Results.Add(result);
            OnPropertyChanged(nameof(HasResults));
            SetStatus(LanguageManager.Keys.PlaylistMatchSummary, matched, tracks.Length, changed, tracks.Length - matched);
        });
    }

    private Task OpenTrackAsync(PlaylistMatchResultRow row)
    {
        var playlist = _host.Get<PlaylistService>().GetRequired(_playlistId);
        var item = playlist.Items.FirstOrDefault(candidate => candidate.Id == row.PlaylistItemId)
                   ?? playlist.Items.FirstOrDefault(candidate => candidate.Track.Id == row.Original.Id);
        if (item is null) return Task.CompletedTask;
        var number = playlist.Items.IndexOf(item) + 1;
        return _navigation.OpenEditorAsync(new TrackRow(item.Track, number, _text, PlaylistId: _playlistId,
            PlaylistItemId: item.Id));
    }

    private void OnMetadataSourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MetadataSourceOption.IsSelected)) RefreshMatchCommand();
    }

    private void RefreshMatchCommand()
    {
        OnPropertyChanged(nameof(CanMatchPlaylist));
        MatchPlaylistCommand?.NotifyCanExecuteChanged();
    }

    private void OnLibraryChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(Playlist));
        OnPropertyChanged(nameof(PageSummary));
        RefreshMatchCommand();
    }

    protected override void RaiseInteractionProperties()
    {
        base.RaiseInteractionProperties();
        RefreshMatchCommand();
        OpenTrackCommand?.NotifyCanExecuteChanged();
    }

    protected override void RefreshLocalizedProperties()
    {
        base.RefreshLocalizedProperties();
        for (var i = 0; i < Results.Count; i++) Results[i] = Results[i].Relocalize(_text);
    }

    public override async ValueTask DisposeAsync()
    {
        Context.LibraryChanged -= OnLibraryChanged;
        foreach (var source in MetadataSources) source.PropertyChanged -= OnMetadataSourceChanged;
        await base.DisposeAsync();
    }
}