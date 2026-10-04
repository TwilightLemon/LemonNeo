using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Desktop.ViewModels;

public sealed partial class TrackEditorPageViewModel : PageViewModel
{
    private readonly DesktopNavigation Navigation;
    private Playlist? _targetPlaylist;
    public TrackEditorPageViewModel(DesktopContext context, DesktopNavigation navigation, PlaybackViewModel playback, TrackRow? row, bool isNew = false) : base(context, DesktopRoutes.Editor)
    {
        Navigation = navigation;
        Editor = row is null ? null : new(row.Track, _text, row.PlaylistId, row.PlaylistItemId, isNew);
        _targetPlaylist = Playlists.FirstOrDefault();
        CloseEditorCommand = new(Navigation.GoBackAsync);
        SaveEditorCommand = new(SaveEditorAsync);
        MetadataCommand = new(() => InspectMetadataAsync(false)); EnrichCommand = new(() => InspectMetadataAsync(true));
        LyricsDetailsCommand = new(InspectLyricsAsync); SourcesCommand = new(InspectSourcesAsync);
        ApplyEnrichmentCommand = new(() =>
        {
            if (!CanInteract || _enrichedPreview is null || Editor is null) return;
            Editor.Apply(_enrichedPreview); SetStatus("EnrichmentApplied");
        });
        PlaySourceCommand = new(() => SelectedSource is { } source && _sourceTrack is { } track
            ? playback.ExecutePlaybackAsync(ct =>
                _runtime.Get<PlaybackSessionService>().PlayTrackAsync(track, new(RequiredSourceProviderId: source.Provider), ct)) : Task.CompletedTask);
        Context.LibraryChanged += OnLibraryChanged;
    }
    public TrackEditorViewModel? Editor { get; }
    public ObservableCollection<Playlist> Playlists => Context.Playlists;
    public Playlist? TargetPlaylist { get => _targetPlaylist; set { if (SetProperty(ref _targetPlaylist, value)) RaiseInteractionProperties(); } }
    public bool CanSaveEditor => CanInteract && Editor is { } editor && (editor.PlaylistId is not null || TargetPlaylist is not null);
    public bool EditorNeedsPlaylist => Editor is { PlaylistId: null };
    public bool HasEnrichedPreview => _enrichedPreview is not null;
    public ObservableCollection<MetadataMatchRow> MetadataMatches { get; } = [];
    public ObservableCollection<PlaybackSourceRow> DiscoveredSources { get; } = [];
    public string MetadataNamespaces => string.Join(", ", _runtime.Get<IProviderRegistry>().Providers.OfType<IMetadataProvider>()
        .SelectMany(p => p.MetadataNamespaces).Distinct(StringComparer.OrdinalIgnoreCase).Order());
    public AsyncRelayCommand CloseEditorCommand { get; }
    public AsyncRelayCommand SaveEditorCommand { get; }
    public AsyncRelayCommand MetadataCommand { get; }
    public AsyncRelayCommand EnrichCommand { get; }
    public RelayCommand ApplyEnrichmentCommand { get; }
    public AsyncRelayCommand LyricsDetailsCommand { get; }
    public AsyncRelayCommand SourcesCommand { get; }
    public AsyncRelayCommand PlaySourceCommand { get; }
    private Track? _sourceTrack;
    private Track? _enrichedPreview;
    private LyricsLookupResult? _inspectedLyrics;
    [ObservableProperty] private string _metadataTargets = "";
    [ObservableProperty] private string _inspectionMessages = "";
    [ObservableProperty] private string _lyricsDetails = "";
    [ObservableProperty] private string _enrichmentSummary = "";
    [ObservableProperty] private PlaybackSourceRow? _selectedSource;
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
            Context.RefreshLibrary();
            if (ReferenceEquals(Navigation.Current, this)) await Navigation.GoBackAsync();
            SetStatus("LibrarySaved");
        });
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
            var lookup = await Task.Run(() => _runtime.Get<MetadataService>().EnrichAsync(track, targets, ct), ct);
            result = lookup.Lookup;
            _enrichedPreview = ReferenceEquals(track, lookup.Track) ? null : lookup.Track;
            EnrichmentSummary = _enrichedPreview is { } preview
                ? $"{preview.Title}\n{string.Join(" / ", preview.Artists.Select(a => a.Name))}\n{preview.Album?.Title}\n{preview.ArtworkUri}\n" +
                    string.Join(Environment.NewLine, preview.ExternalIds.Select(i => $"{i.Namespace}:{i.Kind ?? "-"} = {i.Value}"))
                : _text["NoReliableMatch"];
            OnPropertyChanged(nameof(HasEnrichedPreview));
        }
        else result = await Task.Run(() => _runtime.Get<MetadataService>().LookupAsync(track, targets, cancellationToken: ct), ct);
        MetadataMatches.Clear();
        foreach (var match in result.Matches) MetadataMatches.Add(new(match, _text));
        InspectionMessages = FormatErrors(result.SourceErrors);
        SetStatus("MetadataCompleted", result.Matches.Count);
    });

    public Task InspectLyricsAsync() => ExecuteAsync(async ct =>
    {
        var track = GetInspectionTrack();
        var result = await Task.Run(() => _runtime.Get<LyricsService>().GetLyricsAsync(track, ct), ct);
        _inspectedLyrics = result;
        LyricsDetails = result.Lyrics is { } lyrics ? FormatLyricsDetails(lyrics, result.ProviderId) : _text["NoLyrics"];
        InspectionMessages = FormatErrors(result.ProviderErrors);
    });

    public Task InspectSourcesAsync() => ExecuteAsync(async ct =>
    {
        var track = GetInspectionTrack();
        var result = await Task.Run(() => _runtime.Get<PlaybackSourceDiscoveryService>().FindAsync(track, cancellationToken: ct), ct);
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

    private void OnLibraryChanged(object? sender, EventArgs args)
    {
        TargetPlaylist = Playlists.FirstOrDefault(p => p.Id == TargetPlaylist?.Id) ?? Playlists.FirstOrDefault();
    }
    protected override void RaiseInteractionProperties() { base.RaiseInteractionProperties(); OnPropertyChanged(nameof(CanSaveEditor)); }
    protected override void RefreshLocalizedProperties()
    {
        base.RefreshLocalizedProperties(); OnPropertyChanged(nameof(MetadataNamespaces));
        if (_inspectedLyrics is { } result) LyricsDetails = result.Lyrics is { } lyrics ? FormatLyricsDetails(lyrics, result.ProviderId) : _text["NoLyrics"];
        if (_enrichedPreview is null && !string.IsNullOrEmpty(EnrichmentSummary)) EnrichmentSummary = _text["NoReliableMatch"];
        for (var i = 0; i < MetadataMatches.Count; i++) MetadataMatches[i] = new(MetadataMatches[i].Match, _text);
        for (var i = 0; i < DiscoveredSources.Count; i++) DiscoveredSources[i] = new(DiscoveredSources[i].Candidate, _text);
    }
    public override async ValueTask DisposeAsync() { Context.LibraryChanged -= OnLibraryChanged; await base.DisposeAsync(); }
}
