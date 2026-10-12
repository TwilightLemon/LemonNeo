using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Application.Playback;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.ViewModels.Playback;

/// <summary>Independent, lazily created state for the queue drawer.</summary>
public sealed class PlaybackQueueViewModel : PageViewModel
{
    private readonly PlaybackSessionService _session;
    private readonly DispatcherTimer _filterTimer;
    private string _filter = "";
    private QueueTrackViewModel[] _rows = [];
    private QueueTrackViewModel? _selectedTrack;

    public PlaybackQueueViewModel(DesktopContext context, PlaybackViewModel playback) : base(context,
        DesktopRoutes.Queue)
    {
        Playback = playback;
        _session = _host.Get<PlaybackSessionService>();
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _filterTimer.Tick += OnFilterTick;
        PlayTrackCommand = new AsyncRelayCommand<QueueTrackViewModel>(PlayTrackAsync, row => CanPlay && row is not null,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        Playback.Changed += OnPlaybackChanged;
        Playback.PropertyChanged += OnPlaybackPropertyChanged;
        RefreshQueue();
    }

    public PlaybackViewModel Playback { get; }
    public ObservableCollection<QueueTrackViewModel> Tracks { get; } = [];

    public QueueTrackViewModel? SelectedTrack
    {
        get => _selectedTrack;
        set => SetProperty(ref _selectedTrack, value);
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value)) return;
            _filterTimer.Stop();
            if (string.IsNullOrWhiteSpace(value)) RefreshTracks();
            else _filterTimer.Start();
        }
    }

    public int TrackCount => _rows.Length;
    public bool HasTracks => Tracks.Count > 0;
    public bool HasPlayingTrack => _rows.Any(row => row.IsCurrent);
    public bool CanPlay => CanInteract && Playback.CanInteract;
    public string EmptyTitle => _text[_rows.Length == 0
        ? LanguageManager.Keys.EmptyQueue
        : LanguageManager.Keys.NoResults];
    public string EmptyDescription => _text[_rows.Length == 0
        ? LanguageManager.Keys.EmptyQueueDescription
        : LanguageManager.Keys.NoResultsDescription];
    public AsyncRelayCommand<QueueTrackViewModel> PlayTrackCommand { get; }

    public QueueTrackViewModel? RevealPlayingTrack()
    {
        var row = _rows.FirstOrDefault(row => row.IsCurrent && row.Id == _session.Queue.CurrentItem?.Id)
                  ?? _rows.FirstOrDefault(row => row.IsCurrent);
        if (row is not null && !Tracks.Contains(row)) Filter = "";
        return row;
    }

    public Task PlayTrackAsync(QueueTrackViewModel? row)
    {
        return CanPlay && row is not null
            ? Playback.ExecutePlaybackAsync(ct => _session.PlayQueueItemAsync(row.Id, ct))
            : Task.CompletedTask;
    }

    private void OnPlaybackChanged(object? sender, EventArgs args)
    {
        RefreshQueue();
    }

    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Playback.CanInteract)) RaiseInteractionProperties();
    }

    private void RefreshQueue()
    {
        var items = _session.Queue.Items;
        if (!_rows.Select(r => (r.Id, r.Row.Track)).SequenceEqual(items.Select(i => (i.Id, i.Track))))
        {
            var existing = _rows.ToDictionary(row => row.Id);
            _rows = items.Select((item, index) =>
            {
                if (existing.TryGetValue(item.Id, out var row))
                {
                    row.Update(item, index + 1);
                    return row;
                }
                return new QueueTrackViewModel(item, index + 1, _text, _host.Get<ArtworkService>());
            }).ToArray();
            RefreshTracks();
            OnPropertyChanged(nameof(TrackCount));
        }

        foreach (var row in _rows)
            row.IsCurrent = PlayingTrackMatcher.Matches(row.Row.Track, Playback.Snapshot.SourceDetails?.SourceId);
        OnPropertyChanged(nameof(HasPlayingTrack));
        RaiseInteractionProperties();
    }

    private void RefreshTracks()
    {
        _filterTimer.Stop();
        var selectedId = SelectedTrack?.Id;
        var filter = Filter.Trim();
        var rows = filter.Length == 0 ? _rows : _rows.Where(r =>
            $"{r.Row.Title} {r.Row.Artists} {r.Row.Album}".Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        CollectionSynchronizer.Update(Tracks, rows);
        SelectedTrack = Tracks.FirstOrDefault(r => r.Id == selectedId);
        OnPropertyChanged(nameof(HasTracks));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
    }

    private void OnFilterTick(object? sender, EventArgs args) => RefreshTracks();

    public override Task ActivateAsync()
    {
        RefreshQueue();
        return Task.CompletedTask;
    }

    protected override void RaiseInteractionProperties()
    {
        base.RaiseInteractionProperties();
        OnPropertyChanged(nameof(CanPlay));
        PlayTrackCommand.NotifyCanExecuteChanged();
    }

    protected override void RefreshLocalizedProperties()
    {
        base.RefreshLocalizedProperties();
        foreach (var row in _rows) row.RefreshLocalizedProperties();
        RefreshTracks();
    }

    public override async ValueTask DisposeAsync()
    {
        Playback.Changed -= OnPlaybackChanged;
        Playback.PropertyChanged -= OnPlaybackPropertyChanged;
        _filterTimer.Stop();
        _filterTimer.Tick -= OnFilterTick;
        await base.DisposeAsync();
    }
}
