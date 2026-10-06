using LemonNeo.Application.Playback;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.ViewModels.Playback;

/// <summary>Independent, lazily created state for the queue drawer.</summary>
public sealed class PlaybackQueueViewModel : PageViewModel
{
    private readonly PlaybackSessionService _session;
    private QueueTrackViewModel[] _rows = [];
    private string _filter = "";
    private QueueTrackViewModel? _selectedTrack;

    public PlaybackQueueViewModel(DesktopContext context, PlaybackViewModel playback) : base(context, DesktopRoutes.Queue)
    {
        Playback = playback;
        _session = _host.Get<PlaybackSessionService>();
        PlayTrackCommand = new(PlayTrackAsync, row => CanPlay && row is not null, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        Playback.Changed += OnPlaybackChanged;
        Playback.PropertyChanged += OnPlaybackPropertyChanged;
        RefreshQueue();
    }

    public PlaybackViewModel Playback { get; }
    public ObservableCollection<QueueTrackViewModel> Tracks { get; } = [];
    public QueueTrackViewModel? SelectedTrack { get => _selectedTrack; set => SetProperty(ref _selectedTrack, value); }
    public string Filter { get => _filter; set { if (SetProperty(ref _filter, value)) RefreshTracks(); } }
    public string TrackCount => _text.Format("TrackCount", _rows.Length);
    public bool HasTracks => Tracks.Count > 0;
    public bool CanPlay => CanInteract && Playback.CanInteract;
    public bool CanClearQueue => CanPlay && _rows.Length > 0;
    public string EmptyTitle => _text[_rows.Length == 0 ? "EmptyQueue" : "NoResults"];
    public string EmptyDescription => _text[_rows.Length == 0 ? "EmptyQueueDescription" : "NoResultsDescription"];
    public AsyncRelayCommand<QueueTrackViewModel> PlayTrackCommand { get; }
    public AsyncRelayCommand ClearQueueCommand => Playback.ClearQueueCommand;

    public Task PlayTrackAsync(QueueTrackViewModel? row) => CanPlay && row is not null
        ? Playback.ExecutePlaybackAsync(ct => _session.PlayQueueItemAsync(row.Id, ct)) : Task.CompletedTask;

    private void OnPlaybackChanged(object? sender, EventArgs args) => RefreshQueue();
    private void OnPlaybackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Playback.CanInteract)) RaiseInteractionProperties();
    }

    private void RefreshQueue()
    {
        var items = _session.Queue.Items;
        if (!_rows.Select(r => r.Id).SequenceEqual(items.Select(i => i.Id)))
        {
            _rows = items.Select((item, index) => new QueueTrackViewModel(item, index + 1, _text, _host.Get<ArtworkService>())).ToArray();
            RefreshTracks();
            OnPropertyChanged(nameof(TrackCount));
        }
        var current = _session.Queue.CurrentItem?.Id;
        foreach (var row in _rows) row.IsCurrent = row.Id == current;
        RaiseInteractionProperties();
    }

    private void RefreshTracks()
    {
        var selectedId = SelectedTrack?.Id;
        Tracks.Clear();
        foreach (var row in _rows.Where(r => string.IsNullOrWhiteSpace(Filter) ||
                     $"{r.Row.Title} {r.Row.Artists} {r.Row.Album}".Contains(Filter.Trim(), StringComparison.CurrentCultureIgnoreCase)))
            Tracks.Add(row);
        SelectedTrack = Tracks.FirstOrDefault(r => r.Id == selectedId);
        OnPropertyChanged(nameof(HasTracks)); OnPropertyChanged(nameof(EmptyTitle)); OnPropertyChanged(nameof(EmptyDescription));
    }

    public override Task ActivateAsync() { RefreshQueue(); return Task.CompletedTask; }
    protected override void RaiseInteractionProperties()
    {
        base.RaiseInteractionProperties();
        OnPropertyChanged(nameof(CanPlay)); OnPropertyChanged(nameof(CanClearQueue));
        PlayTrackCommand.NotifyCanExecuteChanged();
    }
    protected override void RefreshLocalizedProperties()
    {
        base.RefreshLocalizedProperties();
        foreach (var row in _rows) row.RefreshLocalizedProperties();
        OnPropertyChanged(nameof(TrackCount)); RefreshTracks();
    }
    public override async ValueTask DisposeAsync()
    {
        Playback.Changed -= OnPlaybackChanged;
        Playback.PropertyChanged -= OnPlaybackPropertyChanged;
        await base.DisposeAsync();
    }
}
