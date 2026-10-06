using LemonNeo.Abstractions.Providers;
using LemonNeo.Application.Library;
using LemonNeo.Application.Providers;
using LemonNeo.Core.Library;
using LemonNeo.Core.Music;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.Infrastructure;

namespace LemonNeo.Desktop.ViewModels;

public sealed partial class MatchPlaylistPageViewModel : PageViewModel
{
    private readonly Guid _playlistId;
    [ObservableProperty] private string _inspectionMessages = "";

    public MatchPlaylistPageViewModel(DesktopContext context, DesktopNavigation navigation, Guid playlistId)
        : base(context, DesktopRoutes.MatchPlaylist)
    {
        _playlistId = playlistId;
        CloseCommand = new(navigation.GoBackAsync);
        MatchPlaylistCommand = new(MatchPlaylistAsync, () => CanMatchPlaylist);
        MetadataSources = MetadataSourceOption.Create(_host.Get<IProviderRegistry>(), _host.Get<IPlaybackSourceProviderRegistry>());
        foreach (var source in MetadataSources) source.PropertyChanged += OnMetadataSourceChanged;
        Context.LibraryChanged += OnLibraryChanged;
    }

    public Playlist? Playlist => Context.Playlists.FirstOrDefault(playlist => playlist.Id == _playlistId);
    public override string? PageSummary => Playlist is { } playlist
        ? $"{playlist.Name} · {_text.Format("TrackSummary", playlist.Items.Count, TrackRow.FormatTime(TimeSpan.FromSeconds(playlist.Items.Sum(item => item.Track.Duration?.TotalSeconds ?? 0))))}"
        : null;
    public IReadOnlyList<MetadataSourceOption> MetadataSources { get; }
    public bool HasMetadataSources => MetadataSources.Count > 0;
    public bool CanMatchPlaylist => CanInteract && Playlist is { Items.Count: > 0 } && MetadataSources.Any(source => source.IsSelected);
    public AsyncRelayCommand CloseCommand { get; }
    public AsyncRelayCommand MatchPlaylistCommand { get; }

    public Task MatchPlaylistAsync()
    {
        if (!CanMatchPlaylist) return Task.CompletedTask;
        // Capture the selection before starting provider work; empty never means "all" in this UI.
        var targets = MetadataSources.Where(source => source.IsSelected).Select(source => source.Namespace).ToArray();
        return ExecuteAsync(async ct =>
        {
            InspectionMessages = "";
            var playlists = _host.Get<PlaylistService>();
            var tracks = playlists.GetRequired(_playlistId).Items.Select(item => item.Track).DistinctBy(track => track.Id).ToArray();
            using var concurrency = new SemaphoreSlim(3);
            var results = await Task.WhenAll(tracks.Select(async track =>
            {
                await concurrency.WaitAsync(ct);
                try { return (Original: track, Result: await Task.Run(() => _host.Get<MetadataService>().EnrichAsync(track, targets, ct), ct)); }
                finally { concurrency.Release(); }
            }));
            ct.ThrowIfCancellationRequested();
            InspectionMessages = string.Join(Environment.NewLine, results.SelectMany(result => result.Result.Lookup.SourceErrors)
                .DistinctBy(error => error.Key).Select(error => $"{error.Key}: {error.Value}"));
            var playlist = playlists.GetRequired(_playlistId);
            var matched = 0;
            foreach (var result in results.Where(result => !ReferenceEquals(result.Original, result.Result.Track)))
            {
                var items = playlist.Items.Where(item => item.Track.Id == result.Original.Id).ToArray();
                foreach (var item in items) playlists.UpdateTrack(_playlistId, item.Id, result.Result.Track);
                if (items.Length > 0) matched++;
            }
            await _host.Get<LibraryService>().SaveAsync(ct);
            Context.RefreshLibrary();
            SetStatus("PlaylistMatched", matched, tracks.Length);
        });
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
    }
    public override async ValueTask DisposeAsync()
    {
        Context.LibraryChanged -= OnLibraryChanged;
        foreach (var source in MetadataSources) source.PropertyChanged -= OnMetadataSourceChanged;
        await base.DisposeAsync();
    }
}
