using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private IReadOnlyList<Track> _searchTracks = [];
    private Track? _sourceTrack;
    private Track? _enrichedPreview;
    private LyricsLookupResult? _inspectedLyrics;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSearch))] private string _searchQuery = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanLogin))] private string _cookie = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanImportRemoteId))] private string _remotePlaylistId = "";
    [ObservableProperty] private string _metadataTargets = "";
    [ObservableProperty] private string _inspectionMessages = "";
    [ObservableProperty] private string _lyricsDetails = "";
    [ObservableProperty] private string _enrichmentSummary = "";
    [ObservableProperty] private ProviderOption? _selectedPlaybackSource;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanBrowseRemote)), NotifyPropertyChangedFor(nameof(CanImportRemoteId))] private ProviderOption? _selectedPlaylistProvider;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanImportRemote))] private RemotePlaylistRow? _selectedRemotePlaylist;
    [ObservableProperty] private PlaybackSourceRow? _selectedSource;
    private ProviderOption? _selectedAuthenticationProvider;
    public ProviderOption? SelectedAuthenticationProvider
    {
        get => _selectedAuthenticationProvider;
        set
        {
            var changedProvider = _selectedAuthenticationProvider?.Id != value?.Id;
            if (SetProperty(ref _selectedAuthenticationProvider, value))
            {
                if (changedProvider) Cookie = "";
                OnPropertyChanged(nameof(AuthenticationStatus));
                OnPropertyChanged(nameof(AuthenticationAccount));
                OnPropertyChanged(nameof(CanLogin)); OnPropertyChanged(nameof(CanLogout));
            }
        }
    }
    public ObservableCollection<ProviderOption> AuthenticationProviders { get; } = [];
    public ObservableCollection<ProviderOption> PlaylistProviders { get; } = [];
    public ObservableCollection<ProviderOption> PlaybackSources { get; } = [];
    public ObservableCollection<RemotePlaylistRow> RemotePlaylists { get; } = [];
    public ObservableCollection<MetadataMatchRow> MetadataMatches { get; } = [];
    public ObservableCollection<PlaybackSourceRow> DiscoveredSources { get; } = [];
    public bool IsSearch => Page == DesktopPage.Search;
    public bool IsOnline => Page == DesktopPage.Online;
    public bool IsEditor => Page == DesktopPage.Editor;
    public bool IsTools => Page == DesktopPage.Tools;
    public bool HasEnrichedPreview => _enrichedPreview is not null;
    public bool CanSearch => CanInteract && !string.IsNullOrWhiteSpace(SearchQuery);
    public bool CanLogin => CanInteract && SelectedAuthenticationProvider?.Id is not null && !string.IsNullOrWhiteSpace(Cookie);
    public bool CanLogout => CanInteract && SelectedAuthenticationProvider?.Id is { } id &&
        _runtime.Get<IProviderRegistry>().Get(id) is IAuthenticationProvider { State.IsAuthenticated: true };
    public bool CanBrowseRemote => CanInteract && SelectedPlaylistProvider?.Id is not null;
    public bool CanImportRemoteId => CanBrowseRemote && !string.IsNullOrWhiteSpace(RemotePlaylistId);
    public bool CanImportRemote => CanInteract && SelectedRemotePlaylist is { CanImport: true };
    public string AuthenticationStatus => SelectedAuthenticationProvider?.Id is { } id &&
        _runtime.Get<IProviderRegistry>().Get(id) is IAuthenticationProvider { State.IsAuthenticated: true }
        ? _text["Authenticated"] : _text["NotAuthenticated"];
    public string AuthenticationAccount => SelectedAuthenticationProvider?.Id is { } id &&
        _runtime.Get<IProviderRegistry>().Get(id) is IAuthenticationProvider { State.IsAuthenticated: true } provider
        ? $"{provider.State.AccountName ?? provider.State.UserId ?? id}  ·  {provider.State.UserId ?? "—"}" : "";
    public string PlaybackDetails => _text.Format("PlaybackDetailsFormat", _snapshot.CurrentTrack?.Id.ToString() ?? "—",
        _snapshot.CurrentIndex + 1, _snapshot.QueueCount, _snapshot.PlayerId ?? "—", _snapshot.RequiredSourceProviderId ?? _text["Automatic"]);
    public AsyncRelayCommand SearchCommand { get; private set; } = null!;
    public AsyncRelayCommand LoginCommand { get; private set; } = null!;
    public AsyncRelayCommand LogoutCommand { get; private set; } = null!;
    public AsyncRelayCommand RemotePlaylistsCommand { get; private set; } = null!;
    public AsyncRelayCommand ImportRemoteCommand { get; private set; } = null!;
    public AsyncRelayCommand ImportRemoteIdCommand { get; private set; } = null!;
    public AsyncRelayCommand MetadataCommand { get; private set; } = null!;
    public AsyncRelayCommand EnrichCommand { get; private set; } = null!;
    public RelayCommand ApplyEnrichmentCommand { get; private set; } = null!;
    public AsyncRelayCommand LyricsDetailsCommand { get; private set; } = null!;
    public AsyncRelayCommand SourcesCommand { get; private set; } = null!;
    public AsyncRelayCommand PlaySourceCommand { get; private set; } = null!;

    private void InitializeFeatureCommands()
    {
        InitializeLibraryCommands();
        InitializeToolCommands();
        SearchCommand = new(SearchAsync);
        LoginCommand = new(LoginAsync);
        LogoutCommand = new(LogoutAsync);
        RemotePlaylistsCommand = new(LoadRemotePlaylistsAsync);
        ImportRemoteCommand = new(() => SelectedRemotePlaylist is { Id: { } id } row ? ImportRemoteAsync(row.ProviderId, id) : Task.CompletedTask);
        ImportRemoteIdCommand = new(() => SelectedPlaylistProvider?.Id is { } provider ? ImportRemoteAsync(provider, RemotePlaylistId) : Task.CompletedTask);
        MetadataCommand = new(() => InspectMetadataAsync(enrich: false));
        EnrichCommand = new(() => InspectMetadataAsync(enrich: true));
        ApplyEnrichmentCommand = new(() =>
        {
            if (!CanInteract || _enrichedPreview is null || Editor is null) return;
            Editor.Apply(_enrichedPreview);
            SetStatus("EnrichmentApplied");
        });
        LyricsDetailsCommand = new(InspectLyricsAsync);
        SourcesCommand = new(InspectSourcesAsync);
        PlaySourceCommand = new(() => SelectedSource is { } source && _sourceTrack is { } track
            ? ExecutePlaybackAsync(ct => _session.PlayTrackAsync(track, new(RequiredSourceProviderId: source.Provider), ct)) : Task.CompletedTask);
    }

    private void RefreshProviderOptions()
    {
        var providers = _runtime.Get<IProviderRegistry>().Providers;
        static ProviderOption Option(IMusicProvider p) => new(p.Id, $"{p.Descriptor.Name} ({p.Id})");
        static ProviderOption? Replace(ObservableCollection<ProviderOption> collection, IEnumerable<ProviderOption> values, ProviderOption? selection)
        {
            collection.Clear();
            foreach (var value in values) collection.Add(value);
            return collection.FirstOrDefault(o => o.Id == selection?.Id) ?? collection.FirstOrDefault();
        }
        SelectedAuthenticationProvider = Replace(AuthenticationProviders, providers.Where(p => p is IAuthenticationProvider).Select(Option), SelectedAuthenticationProvider);
        SelectedPlaylistProvider = Replace(PlaylistProviders, providers.Where(p => p is IPlaylistProvider).Select(Option), SelectedPlaylistProvider);
        SelectedPlaybackSource = Replace(PlaybackSources, new[] { new ProviderOption(null, _text["Automatic"]) }
            .Concat(_runtime.Get<IPlaybackSourceProviderRegistry>().Sources.Select(s => Option(s.MusicProvider))), SelectedPlaybackSource);
        RefreshSyncProviders();
    }

    private PlaybackRequest CreatePlaybackRequest()
    {
        var id = SelectedPlaybackSource?.Id;
        if (id is not null && _runtime.Get<IPlaybackSourceProviderRegistry>().Get(id) is null)
            throw new ArgumentException(_text["ProviderUnavailable"]);
        return new(RequiredSourceProviderId: id);
    }

    public Task SearchAsync() => ExecuteAsync(async ct =>
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) throw new ArgumentException(_text["SearchQueryRequired"]);
        var results = await _runtime.Get<SearchService>().SearchForPlaybackAsync(SearchQuery.Trim(), ct);
        ct.ThrowIfCancellationRequested();
        _searchTracks = results.SelectMany(r => r.Tracks).DistinctBy(t => t.Id).ToArray();
        Page = DesktopPage.Search;
        Filter = "";
        RefreshTracks();
        SetStatus("SearchCompleted", Tracks.Count);
    });

    public Task LoginAsync() => ExecuteAsync(async ct =>
    {
        try
        {
            var provider = GetAuthenticationProvider();
            if (!(await provider.GetAvailableMethodsAsync(ct)).Contains(AuthMethod.Cookie))
                throw new ArgumentException(_text["CookieUnsupported"]);
            if (string.IsNullOrWhiteSpace(Cookie)) throw new ArgumentException(_text["CookieRequired"]);
            await provider.AuthenticateAsync(new(AuthMethod.Cookie, new Dictionary<string, string> { ["cookie"] = Cookie.Trim() }), ct);
            if (PlaylistProviders.FirstOrDefault(p => p.Id == SelectedAuthenticationProvider?.Id) is { } playlists)
                SelectedPlaylistProvider = playlists;
            SetStatus("LoginCompleted");
        }
        finally
        {
            Cookie = "";
            OnPropertyChanged(nameof(AuthenticationStatus)); OnPropertyChanged(nameof(AuthenticationAccount));
        }
    });

    public Task LogoutAsync() => ExecuteAsync(async ct =>
    {
        await GetAuthenticationProvider().SignOutAsync(ct);
        Cookie = "";
        RemotePlaylists.Clear();
        SelectedRemotePlaylist = null;
        OnPropertyChanged(nameof(AuthenticationStatus)); OnPropertyChanged(nameof(AuthenticationAccount));
        SetStatus("LogoutCompleted");
    });

    private IAuthenticationProvider GetAuthenticationProvider() => SelectedAuthenticationProvider?.Id is { } id &&
        _runtime.Get<IProviderRegistry>().Get(id) is IAuthenticationProvider provider
        ? provider : throw new ArgumentException(_text["ProviderUnavailable"]);

    partial void OnSelectedPlaylistProviderChanged(ProviderOption? oldValue, ProviderOption? newValue)
    {
        if (oldValue?.Id == newValue?.Id) return;
        RemotePlaylists.Clear(); SelectedRemotePlaylist = null;
    }

    public Task LoadRemotePlaylistsAsync() => ExecuteAsync(async ct =>
    {
        if (SelectedPlaylistProvider?.Id is not { } id || _runtime.Get<IProviderRegistry>().Get(id) is not IPlaylistProvider provider)
            throw new ArgumentException(_text["ProviderUnavailable"]);
        var rows = new List<RemotePlaylistRow>();
        if (provider is IPlaylistCollectionProvider groups)
        {
            await foreach (var collection in groups.GetUserPlaylistCollectionsAsync(ct))
            {
                if (collection.Playlists.Count == 0) rows.Add(new(id, null, _text["EmptyCollection"], collection.Name, "—"));
                foreach (var playlist in collection.Playlists)
                    rows.Add(new(id, playlist.Id, playlist.Name, collection.Name, playlist.TrackCount?.ToString(_text.Culture) ?? "—"));
            }
        }
        else
        {
            await foreach (var playlist in provider.GetUserPlaylistsAsync(ct))
                rows.Add(new(id, playlist.Id, playlist.Name, _text["RemotePlaylists"], playlist.TrackCount?.ToString(_text.Culture) ?? "—"));
        }
        ct.ThrowIfCancellationRequested();
        RemotePlaylists.Clear();
        foreach (var row in rows) RemotePlaylists.Add(row);
        SelectedRemotePlaylist = RemotePlaylists.FirstOrDefault(r => r.CanImport);
        SetStatus("RemotePlaylistsLoaded", rows.Count(r => r.CanImport));
    });

    public Task ImportRemoteAsync(string providerId, string id) => ExecuteAsync(async ct =>
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException(_text["RemoteIdRequired"]);
        var playlist = await _runtime.Get<ImportService>().ImportAsync(providerId, id.Trim(), ct);
        await _runtime.Get<LibraryService>().SaveAsync(ct);
        RefreshLibrary(playlist.Id);
        SetStatus("RemoteImported", playlist.Name, playlist.Items.Count);
    });

    private void ResetInspection()
    {
        MetadataMatches.Clear(); DiscoveredSources.Clear(); SelectedSource = null; _sourceTrack = null;
        LyricsDetails = ""; InspectionMessages = ""; EnrichmentSummary = ""; _enrichedPreview = null; _inspectedLyrics = null;
        OnPropertyChanged(nameof(HasEnrichedPreview));
    }

    private Track GetInspectionTrack() => Editor?.Build(validateExternalIdSchemes: false) ?? throw new ArgumentException(_text["SelectTrackFirst"]);
    private static string[] ParseNamespaces(string text) => text.Split([',', ';', ' ', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static string FormatErrors(IEnumerable<KeyValuePair<string, string>> errors) => string.Join(Environment.NewLine, errors.Select(e => $"{e.Key}: {e.Value}"));

    public Task InspectMetadataAsync(bool enrich) => ExecuteAsync(async ct =>
    {
        var track = GetInspectionTrack();
        var targets = ParseNamespaces(MetadataTargets);
        MetadataLookupResult result;
        if (enrich)
        {
            var lookup = await _runtime.Get<MetadataService>().EnrichAsync(track, targets, ct);
            result = lookup.Lookup;
            _enrichedPreview = ReferenceEquals(track, lookup.Track) ? null : lookup.Track;
            EnrichmentSummary = _enrichedPreview is { } preview
                ? $"{preview.Title}\n{string.Join(" / ", preview.Artists.Select(a => a.Name))}\n{preview.Album?.Title}\n{preview.ArtworkUri}\n" +
                    string.Join(Environment.NewLine, preview.ExternalIds.Select(i => $"{i.Namespace}:{i.Kind ?? "-"} = {i.Value}"))
                : _text["NoReliableMatch"];
            OnPropertyChanged(nameof(HasEnrichedPreview));
        }
        else result = await _runtime.Get<MetadataService>().LookupAsync(track, targets, cancellationToken: ct);
        MetadataMatches.Clear();
        foreach (var match in result.Matches) MetadataMatches.Add(new(match, _text));
        InspectionMessages = FormatErrors(result.SourceErrors);
        SetStatus("MetadataCompleted", result.Matches.Count);
    });

    public Task InspectLyricsAsync() => ExecuteAsync(async ct =>
    {
        var result = await _runtime.Get<LyricsService>().GetLyricsAsync(GetInspectionTrack(), ct);
        _inspectedLyrics = result;
        LyricsDetails = result.Lyrics is { } lyrics ? FormatLyricsDetails(lyrics, result.ProviderId) : _text["NoLyrics"];
        InspectionMessages = FormatErrors(result.ProviderErrors);
    });

    public Task InspectSourcesAsync() => ExecuteAsync(async ct =>
    {
        var track = GetInspectionTrack();
        var result = await _runtime.Get<PlaybackSourceDiscoveryService>().FindAsync(track, cancellationToken: ct);
        DiscoveredSources.Clear();
        foreach (var candidate in result.Candidates) DiscoveredSources.Add(new(candidate, _text));
        _sourceTrack = track;
        SelectedSource = DiscoveredSources.FirstOrDefault();
        InspectionMessages = FormatErrors(result.ProviderErrors);
        SetStatus("SourcesCompleted", result.Candidates.Count);
    });

    private string FormatLyricsDetails(Lyrics lyrics, string? providerId)
    {
        var text = new StringBuilder(_text.Format("LyricsDetailsSummary", providerId ?? "—", lyrics.Source ?? "—",
            _text[lyrics.IsSynchronized ? "Yes" : "No"], _text[lyrics.IsWordSynchronized ? "Yes" : "No"])).AppendLine();
        if (lyrics.SourceId is { } source) text.AppendLine($"{source.Namespace}:{source.Kind ?? "-"} = {source.Value}");
        if (lyrics.Lines is { Count: > 0 })
            foreach (var line in lyrics.Lines)
            {
                text.AppendLine($"{LyricTime(line.EffectiveStartTimeMs)}{line.Text}");
                if (line.Translations is not null) foreach (var translation in line.Translations.Values) text.AppendLine(translation);
                if (!string.IsNullOrWhiteSpace(line.Romanization)) text.AppendLine(line.Romanization);
            }
        else text.AppendLine(lyrics.Text);
        text.AppendLine().AppendLine(_text["TranslationLabel"]);
        if (lyrics.TranslationLines is { Count: > 0 }) foreach (var line in lyrics.TranslationLines) text.AppendLine($"{LyricTime(line.EffectiveStartTimeMs)}{line.Text}");
        else if (!string.IsNullOrWhiteSpace(lyrics.Translation)) text.AppendLine(lyrics.Translation);
        text.AppendLine().AppendLine(_text["RomanizationLabel"]);
        if (lyrics.RomanizationLines is { Count: > 0 }) foreach (var line in lyrics.RomanizationLines) text.AppendLine($"{LyricTime(line.StartTimeMs)}{line.Text}");
        else if (!string.IsNullOrWhiteSpace(lyrics.Romanization)) text.AppendLine(lyrics.Romanization);
        return text.ToString();
    }
    private static string LyricTime(int? milliseconds) => milliseconds is { } time ? $"[{TimeSpan.FromMilliseconds(time):mm\\:ss\\.fff}] " : "";

    private void RefreshLocalizedResults()
    {
        if (_inspectedLyrics is { } result) LyricsDetails = result.Lyrics is { } lyrics ? FormatLyricsDetails(lyrics, result.ProviderId) : _text["NoLyrics"];
        if (_enrichedPreview is null && !string.IsNullOrEmpty(EnrichmentSummary)) EnrichmentSummary = _text["NoReliableMatch"];
        for (var i = 0; i < MetadataMatches.Count; i++) MetadataMatches[i] = new(MetadataMatches[i].Match, _text);
        for (var i = 0; i < DiscoveredSources.Count; i++) DiscoveredSources[i] = new(DiscoveredSources[i].Candidate, _text);
        for (var i = 0; i < RemotePlaylists.Count; i++)
            if (!RemotePlaylists[i].CanImport) RemotePlaylists[i] = RemotePlaylists[i] with { Name = _text["EmptyCollection"] };
        RefreshSyncText();
    }
}
