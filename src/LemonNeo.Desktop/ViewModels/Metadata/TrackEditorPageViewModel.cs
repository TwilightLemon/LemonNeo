using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Application.Library;
using LemonNeo.Application.Lyrics;
using LemonNeo.Application.Playback;
using LemonNeo.Application.Providers;
using LemonNeo.Core.Library;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Playback;
using LemonNeo.Desktop.ViewModels.Shared;
using CoreLyrics = LemonNeo.Core.Lyrics.Lyrics;

namespace LemonNeo.Desktop.ViewModels.Metadata;

public sealed partial class TrackEditorPageViewModel : PageViewModel
{
    private readonly DesktopNavigation Navigation;
    private LyricsLookupResult? _inspectedLyrics;
    [ObservableProperty] private string _inspectionMessages = "";
    [ObservableProperty] private string _lyricsDetails = "";
    [ObservableProperty] private string _metadataSearchQuery = "";
    [ObservableProperty] private PlaybackSourceRow? _selectedSource;
    private Track? _sourceTrack;
    private Playlist? _targetPlaylist;

    public TrackEditorPageViewModel(DesktopContext context, DesktopNavigation navigation, PlaybackViewModel playback,
        TrackRow? row, bool isNew = false) : base(context, DesktopRoutes.Editor)
    {
        Navigation = navigation;
        Editor = row is null
            ? null
            : new TrackEditorViewModel(row.Track, _text, row.PlaylistId, row.PlaylistItemId, isNew);
        _targetPlaylist = Playlists.FirstOrDefault();
        CloseEditorCommand = new AsyncRelayCommand(Navigation.GoBackAsync);
        SaveEditorCommand = new AsyncRelayCommand(SaveEditorAsync);
        MetadataCommand = new AsyncRelayCommand(InspectMetadataAsync, () => CanFindMetadata);
        UseTrackSearchQueryCommand = new RelayCommand(UseTrackSearchQuery, () => CanInteract && Editor is not null);
        ReplaceMatchMetadataCommand = new RelayCommand<MetadataMatchRow>(row =>
        {
            if (!CanUseMetadataMatch(row) || string.IsNullOrWhiteSpace(row!.Match.Track.Title)) return;
            Editor!.ApplyMetadata(row!.Match.Track);
            row.MarkMetadataReplaced();
            SetStatus("MetadataApplied");
        }, row => CanUseMetadataMatch(row) && !string.IsNullOrWhiteSpace(row!.Match.Track.Title));
        AddMatchExternalIdsCommand = new RelayCommand<MetadataMatchRow>(row =>
        {
            if (!CanUseMetadataMatch(row)) return;
            var added = Editor!.AddExternalIds(row!.Match.Track.ExternalIds);
            row.MarkExternalIdsAdded();
            SetStatus("MetadataIdsAdded", added);
        }, row => CanUseMetadataMatch(row) && row!.Match.Track.ExternalIds.Count > 0);
        MetadataSources = MetadataSourceOption.Create(_host.Get<IProviderRegistry>(),
            _host.Get<IPlaybackSourceProviderRegistry>());
        foreach (var source in MetadataSources) source.PropertyChanged += OnMetadataSourceChanged;
        UseTrackSearchQuery();
        LyricsDetailsCommand = new AsyncRelayCommand(InspectLyricsAsync);
        SourcesCommand = new AsyncRelayCommand(InspectSourcesAsync);
        PlaySourceCommand = new AsyncRelayCommand(() => SelectedSource is { } source && _sourceTrack is { } track
            ? playback.ExecutePlaybackAsync(ct =>
                _host.Get<PlaybackSessionService>().PlayTrackAsync(track, new PlaybackRequest(
                    source.Candidate.Source.Quality ?? AudioQuality.Automatic,
                    source.Candidate.IsCached ? null : source.Provider, source.Candidate.SourceId), ct))
            : Task.CompletedTask);
        Context.LibraryChanged += OnLibraryChanged;
    }

    public TrackEditorViewModel? Editor { get; }
    public ObservableCollection<Playlist> Playlists => Context.Playlists;

    public Playlist? TargetPlaylist
    {
        get => _targetPlaylist;
        set
        {
            if (SetProperty(ref _targetPlaylist, value)) RaiseInteractionProperties();
        }
    }

    public bool CanSaveEditor => CanInteract && Editor is { } editor &&
                                 (editor.PlaylistId is not null || TargetPlaylist is not null);

    public bool EditorNeedsPlaylist => Editor is { PlaylistId: null };
    public IReadOnlyList<MetadataSourceOption> MetadataSources { get; }
    public bool HasMetadataSources => MetadataSources.Count > 0;

    public bool CanFindMetadata => CanInteract && Editor is not null && !string.IsNullOrWhiteSpace(MetadataSearchQuery)
                                   && MetadataSources.Any(source => source.IsSelected);

    public ObservableCollection<MetadataMatchRow> MetadataMatches { get; } = [];
    public ObservableCollection<PlaybackSourceRow> DiscoveredSources { get; } = [];
    public AsyncRelayCommand CloseEditorCommand { get; }
    public AsyncRelayCommand SaveEditorCommand { get; }
    public AsyncRelayCommand MetadataCommand { get; }
    public RelayCommand UseTrackSearchQueryCommand { get; }
    public RelayCommand<MetadataMatchRow> ReplaceMatchMetadataCommand { get; }
    public RelayCommand<MetadataMatchRow> AddMatchExternalIdsCommand { get; }
    public AsyncRelayCommand LyricsDetailsCommand { get; }
    public AsyncRelayCommand SourcesCommand { get; }
    public AsyncRelayCommand PlaySourceCommand { get; }

    public Task SaveEditorAsync()
    {
        if (!CanSaveEditor || Editor is not { } editor) return Task.CompletedTask;
        var destination = editor.PlaylistId ?? TargetPlaylist!.Id;
        return ExecuteAsync(async ct =>
        {
            var track = editor.Build();
            var service = _host.Get<PlaylistService>();
            if (editor.ItemId is { } itemId) service.UpdateTrack(destination, itemId, track);
            else editor.AttachItem(destination, service.Add(destination, track).Id);
            await _host.Get<LibraryService>().SaveAsync(ct);
            // Stay in the editor on validation or persistence failure.
            Context.RefreshLibrary();
            if (ReferenceEquals(Navigation.Current, this)) await Navigation.GoBackAsync();
            SetStatus("LibrarySaved");
        });
    }

    private Track GetInspectionTrack()
    {
        return Editor?.Build(false) ??
               throw new ArgumentException(_text["SelectTrackFirst"]);
    }

    private static string FormatErrors(IEnumerable<KeyValuePair<string, string>> errors)
    {
        return string.Join(Environment.NewLine, errors.Select(e => $"{e.Key}: {e.Value}"));
    }

    private bool CanUseMetadataMatch(MetadataMatchRow? row)
    {
        return CanInteract && Editor is not null && row is not null
               && MetadataMatches.Any(candidate => ReferenceEquals(candidate, row));
    }

    private void UseTrackSearchQuery()
    {
        MetadataSearchQuery = Editor is { } editor
            ? string.Join(" ",
                new[] { editor.Title.Trim() }.Concat(editor.Artists.Split(';',
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
            : "";
    }

    partial void OnMetadataSearchQueryChanged(string value)
    {
        RefreshMetadataCommands();
    }

    private void OnMetadataSourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MetadataSourceOption.IsSelected)) RefreshMetadataCommands();
    }

    private void RefreshMetadataCommands()
    {
        OnPropertyChanged(nameof(CanFindMetadata));
        MetadataCommand?.NotifyCanExecuteChanged();
        UseTrackSearchQueryCommand?.NotifyCanExecuteChanged();
        ReplaceMatchMetadataCommand?.NotifyCanExecuteChanged();
        AddMatchExternalIdsCommand?.NotifyCanExecuteChanged();
    }

    public Task InspectMetadataAsync()
    {
        return ExecuteAsync(async ct =>
        {
            var track = GetInspectionTrack();
            if (string.IsNullOrWhiteSpace(MetadataSearchQuery))
                throw new ArgumentException(_text["SearchQueryRequired"]);
            var targets = MetadataSources.Where(source => source.IsSelected).Select(source => source.Namespace)
                .ToArray();
            if (targets.Length == 0) throw new ArgumentException(_text["SelectMetadataSources"]);
            var query = MetadataSearchQuery.Trim();
            var result = await Task.Run(() => _host.Get<MetadataService>().LookupAsync(track, targets,
                20,
                0, cancellationToken: ct, searchKeyword: query), ct);
            ct.ThrowIfCancellationRequested();
            MetadataMatches.Clear();
            foreach (var match in result.Matches)
                MetadataMatches.Add(
                    new MetadataMatchRow(match, _text, ReplaceMatchMetadataCommand, AddMatchExternalIdsCommand)
                        { ArtworkLoader = _host.Get<ArtworkService>() });
            RefreshMetadataCommands();
            InspectionMessages = FormatErrors(result.SourceErrors);
            SetStatus("MetadataCompleted", result.Matches.Count);
        });
    }

    public Task InspectLyricsAsync()
    {
        return ExecuteAsync(async ct =>
        {
            var track = GetInspectionTrack();
            var result = await Task.Run(() => _host.Get<LyricsService>().GetLyricsAsync(track, ct), ct);
            _inspectedLyrics = result;
            LyricsDetails = result.Lyrics is { } lyrics
                ? FormatLyricsDetails(lyrics, result.ProviderId)
                : _text["NoLyrics"];
            InspectionMessages = FormatErrors(result.ProviderErrors);
        });
    }

    public Task InspectSourcesAsync()
    {
        return ExecuteAsync(async ct =>
        {
            var track = GetInspectionTrack();
            var result =
                await Task.Run(
                    () => _host.Get<PlaybackSourceDiscoveryService>().FindKnownAsync(track, cancellationToken: ct), ct);
            DiscoveredSources.Clear();
            foreach (var candidate in result.Candidates) DiscoveredSources.Add(new PlaybackSourceRow(candidate, _text));
            _sourceTrack = track;
            SelectedSource = DiscoveredSources.FirstOrDefault();
            InspectionMessages = FormatErrors(result.ProviderErrors);
            SetStatus("SourcesCompleted", result.Candidates.Count);
        });
    }

    private string FormatLyricsDetails(CoreLyrics lyrics, string? providerId)
    {
        var text = new StringBuilder(_text.Format("LyricsDetailsSummary", providerId ?? "—", lyrics.Source ?? "—",
            _text[lyrics.IsSynchronized ? "Yes" : "No"], _text[lyrics.IsWordSynchronized ? "Yes" : "No"])).AppendLine();
        if (lyrics.SourceId is { } source) text.AppendLine($"{source.Namespace}:{source.Kind ?? "-"} = {source.Value}");
        if (lyrics.Lines is { Count: > 0 })
            foreach (var line in lyrics.Lines)
            {
                text.AppendLine($"{LyricTime(line.EffectiveStartTimeMs)}{line.Text}");
                if (line.Translations is not null)
                    foreach (var translation in line.Translations.Values)
                        text.AppendLine(translation);
                if (!string.IsNullOrWhiteSpace(line.Romanization)) text.AppendLine(line.Romanization);
            }
        else text.AppendLine(lyrics.Text);

        text.AppendLine().AppendLine(_text["TranslationLabel"]);
        if (lyrics.TranslationLines is { Count: > 0 })
            foreach (var line in lyrics.TranslationLines)
                text.AppendLine($"{LyricTime(line.EffectiveStartTimeMs)}{line.Text}");
        else if (!string.IsNullOrWhiteSpace(lyrics.Translation)) text.AppendLine(lyrics.Translation);
        text.AppendLine().AppendLine(_text["RomanizationLabel"]);
        if (lyrics.RomanizationLines is { Count: > 0 })
            foreach (var line in lyrics.RomanizationLines)
                text.AppendLine($"{LyricTime(line.StartTimeMs)}{line.Text}");
        else if (!string.IsNullOrWhiteSpace(lyrics.Romanization)) text.AppendLine(lyrics.Romanization);
        return text.ToString();
    }

    private static string LyricTime(int? milliseconds)
    {
        return milliseconds is { } time ? $"[{TimeSpan.FromMilliseconds(time):mm\\:ss\\.fff}] " : "";
    }

    private void OnLibraryChanged(object? sender, EventArgs args)
    {
        TargetPlaylist = Playlists.FirstOrDefault(p => p.Id == TargetPlaylist?.Id) ?? Playlists.FirstOrDefault();
    }

    protected override void RaiseInteractionProperties()
    {
        base.RaiseInteractionProperties();
        OnPropertyChanged(nameof(CanSaveEditor));
        RefreshMetadataCommands();
    }

    protected override void RefreshLocalizedProperties()
    {
        base.RefreshLocalizedProperties();
        if (_inspectedLyrics is { } result)
            LyricsDetails = result.Lyrics is { } lyrics
                ? FormatLyricsDetails(lyrics, result.ProviderId)
                : _text["NoLyrics"];
        foreach (var row in MetadataMatches) row.RefreshLocalizedProperties();
        for (var i = 0; i < DiscoveredSources.Count; i++)
            DiscoveredSources[i] = new PlaybackSourceRow(DiscoveredSources[i].Candidate, _text);
    }

    public override async ValueTask DisposeAsync()
    {
        Context.LibraryChanged -= OnLibraryChanged;
        foreach (var source in MetadataSources) source.PropertyChanged -= OnMetadataSourceChanged;
        await base.DisposeAsync();
    }
}