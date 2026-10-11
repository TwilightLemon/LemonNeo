using CommunityToolkit.Mvvm.ComponentModel;
using LemonNeo.Core.Playback;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.ViewModels.Playback;

public sealed class QueueTrackViewModel(
    PlaybackQueueItem item,
    int number,
    LocalizationService text,
    ArtworkService? artwork = null) : ObservableObject
{
    private bool _isCurrent;
    public Guid Id { get; } = item.Id;
    public TrackRow Row { get; } = new(item.Track, number, text, item.Id) { ArtworkLoader = artwork };

    public bool IsCurrent
    {
        get => _isCurrent;
        internal set => SetProperty(ref _isCurrent, value);
    }

    public string Description => $"{Row.Artists} · {Row.Album}";

    internal void RefreshLocalizedProperties()
    {
        OnPropertyChanged(nameof(Row));
        OnPropertyChanged(nameof(Description));
    }
}