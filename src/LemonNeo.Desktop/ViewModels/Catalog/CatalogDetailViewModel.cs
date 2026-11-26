using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Application.Library;
using LemonNeo.Application.Playback;
using LemonNeo.Application.Providers;
using LemonNeo.Core.Exceptions;
using LemonNeo.Core.Music;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Playback;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.ViewModels.Catalog;

public sealed partial class CatalogDetailViewModel : TrackListViewModel
{
    private AlbumDetail? _album;
    private bool _loaded;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    private string _name;
    [ObservableProperty] private Uri? _artworkUri;

    public CatalogDetailViewModel(DesktopContext context, DesktopNavigation navigation, PlaybackViewModel playback,
        CatalogItem item) : base(context, navigation, playback,
        item.Kind == SearchKind.Album ? DesktopRoutes.Album : DesktopRoutes.Artist)
    {
        Item = item;
        _name = item.Name;
        _artworkUri = IsAlbum ? null : item.ArtworkUri;
        ArtworkLoader = _host.Get<ArtworkService>();
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        ReloadCommand = new AsyncRelayCommand(LoadAsync);
    }

    public CatalogItem Item { get; }
    public ArtworkService ArtworkLoader { get; }
    public bool IsAlbum => Item.Kind == SearchKind.Album;
    public bool CanImport => CanInteract && _album is not null;
    public bool CanReload => CanInteract && !_loaded;
    public override string PageTitle => IsAlbum ? Name : base.PageTitle;
    public override string? PageSummary => IsAlbum && _loaded ? TrackSummary : null;
    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand ReloadCommand { get; }

    public override async Task ActivateAsync()
    {
        await base.ActivateAsync();
        if (!_loaded) await LoadAsync();
    }

    protected override IEnumerable<TrackRow> GetTracks() =>
        _album?.Tracks.DistinctBy(track => track.Id).Select((track, index) => new TrackRow(track, index + 1, _text)) ?? [];

    public override Task PlayAsync(TrackRow? selected = null)
    {
        if (_album is not { Tracks.Count: > 0 } album) return Task.CompletedTask;
        var index = selected is null ? 0 : Math.Max(0, album.Tracks.ToList().FindIndex(track => track.Id == selected.Track.Id));
        return Playback.ExecutePlaybackAsync(ct =>
            _host.Get<PlaybackSessionService>().PlayPlaylistAsync(album.Tracks, index, cancellationToken: ct));
    }

    private Task LoadAsync() => ExecuteAsync(async ct =>
    {
        var id = Item.Id ?? await ResolveItemIdAsync(ct);
        var provider = _host.Get<IProviderRegistry>().Get(id.ProviderId) as ICatalogProvider
                       ?? throw new ProviderException(_text[LanguageManager.Keys.ProviderUnavailable]);
        if (IsAlbum)
        {
            _album = await provider.GetAlbumAsync(id.RemoteId, ct)
                     ?? throw new ProviderException(_text[LanguageManager.Keys.CatalogItemUnavailable]);
            Name = _album.Album.Title;
        }
        else if (await provider.GetArtistAsync(id.RemoteId, ct) is { } artist)
        {
            Name = artist.Name;
            ArtworkUri = artist.ArtworkUri ?? Item.ArtworkUri;
        }
        _loaded = true;
        RefreshTracks();
    });

    private async Task<MusicItemId> ResolveItemIdAsync(CancellationToken ct)
    {
        var lookup = _host.Get<TrackLookupService>();
        var registry = _host.Get<IProviderRegistry>();
        ProviderException? lookupError = null;
        foreach (var candidate in Item.SourceTrackIds)
        {
            Track? track;
            try
            {
                track = await lookup.GetTrackAsync(candidate.ProviderId, candidate.RemoteId, ct);
            }
            catch (ProviderException exception)
            {
                lookupError ??= exception;
                continue;
            }

            var resolved = IsAlbum
                ? track?.Album is { } album && MatchesName(album.Title) ? album.Id : null
                : track?.Artists.FirstOrDefault(artist => MatchesName(artist.Name))?.Id;
            if (resolved is { } id && registry.Get(id.ProviderId) is ICatalogProvider) return id;
        }

        throw new ProviderException(_text[LanguageManager.Keys.CatalogItemUnavailable], lookupError);
    }

    private bool MatchesName(string name) => name.Trim().Equals(Item.Name.Trim(), StringComparison.OrdinalIgnoreCase);

    private Task ImportAsync() => ExecuteAsync(async ct =>
    {
        if (_album is not { } album) return;
        ct.ThrowIfCancellationRequested();
        var library = _host.Get<LibraryService>();
        var local = _host.Get<ImportService>().ImportRemote(new RemotePlaylist
        {
            Id = album.Album.Id.RemoteId,
            ProviderId = album.Album.Id.ProviderId,
            Name = album.Album.Title,
            Description = album.Description,
            ArtworkUri = album.Album.ArtworkUri ?? Item.ArtworkUri,
            Tracks = album.Tracks
        });
        try
        {
            await library.SaveAsync(ct);
        }
        catch
        {
            _host.Get<PlaylistService>().Delete(local.Id);
            throw;
        }
        Context.RefreshLibrary();
        Notify(NotificationLevel.Success, LanguageManager.Keys.RemoteImported, local.Name, local.Items.Count);
    });

    protected override void RaiseInteractionProperties()
    {
        base.RaiseInteractionProperties();
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(CanReload));
    }
}
