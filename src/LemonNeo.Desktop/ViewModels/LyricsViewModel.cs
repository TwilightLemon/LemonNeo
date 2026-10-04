using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Desktop.ViewModels;

public sealed class LyricsViewModel : PageViewModel
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _lyricsCancellation;
    private readonly List<Task> _lyricsTasks = [];
    private PlaybackSessionSnapshot _snapshot;
    private Lyrics? _lyrics;
    private Track? _lyricsTrack;
    private string _lyricsStatusKey = "NoLyrics";
    private IReadOnlyList<LyricsLine> _lyricLines = [];
    public LyricsViewModel(DesktopContext context, PlaybackViewModel playback) : base(context, DesktopRoutes.Lyrics)
    {
        Playback = playback; _snapshot = playback.Snapshot;
        Playback.Changed += OnPlaybackChanged;
        UpdateSnapshot();
    }
    public PlaybackViewModel Playback { get; }
    public string LyricsText => _lyrics?.Text ?? _text[_lyricsStatusKey];
    public string CurrentLyric { get; private set; } = "";
    public string PreviousLyric { get; private set; } = "";
    public string NextLyric { get; private set; } = "";
    public string Translation { get; private set; } = "";
    public bool HasTimedLyrics => _lyricLines.Any(l => l.EffectiveStartTimeMs is not null);
    private IReadOnlyList<LyricsDisplaySegment> _currentLyricSegments = [];
    private IReadOnlyList<LyricsDisplaySegment> _romanizationSegments = [];
    public IReadOnlyList<LyricsDisplaySegment> CurrentLyricSegments => _currentLyricSegments;
    public IReadOnlyList<LyricsDisplaySegment> RomanizationSegments => _romanizationSegments;
    public bool HasRomanization => RomanizationSegments.Count > 0;
    public bool HasTranslation => !string.IsNullOrWhiteSpace(Translation);

    private void OnPlaybackChanged(object? sender, EventArgs args) => UpdateSnapshot();
    private void UpdateSnapshot()
    {
        _snapshot = Playback.Snapshot;
        UpdateLyricsPosition();
        if (Equals(_lyricsTrack, _snapshot.CurrentTrack)) return;
        _lyricsTrack = _snapshot.CurrentTrack;
        _lyricsCancellation?.Cancel(); _lyricsCancellation?.Dispose();
        _lyricsCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _lyricsTasks.RemoveAll(t => t.IsCompleted);
        _lyricsTasks.Add(LoadLyricsAsync(_snapshot.CurrentTrack, _lyricsCancellation.Token));
    }
    private async Task LoadLyricsAsync(Track? track, CancellationToken cancellationToken)
    {
        _lyrics = null;
        _lyricLines = [];
        _lyricsStatusKey = track is null ? "NoLyrics" : "LyricsLoading";
        RaiseLyricsProperties();
        if (track is null) return;
        try
        {
            var result = await Task.Run(() => _runtime.Get<LyricsService>().GetLyricsAsync(track, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!Equals(_snapshot.CurrentTrack, track)) return;
            _lyrics = result.Lyrics;
            _lyricsStatusKey = "NoLyrics";
            _lyricLines = _lyrics?.Lines?.OrderBy(l => l.EffectiveStartTimeMs ?? int.MaxValue).ToArray() ?? [];
            UpdateLyricsPosition();
            RaiseLyricsProperties();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) { if (!cancellationToken.IsCancellationRequested) { _lyricsStatusKey = "LyricsError"; RaiseLyricsProperties(); } }
    }

    private void UpdateLyricsPosition()
    {
        var milliseconds = _snapshot.Timeline.Position.TotalMilliseconds;
        var index = -1;
        for (var i = 0; i < _lyricLines.Count; i++)
            if (_lyricLines[i].EffectiveStartTimeMs is { } start && start <= milliseconds) index = i;
        var frame = _lyrics is not null && _snapshot.State != PlayerState.Stopped
            ? LyricsTimeline.CreateFrame(_lyrics, milliseconds) : new LyricsDisplayFrame(LyricsDisplayRow.Empty, null, null);
        CurrentLyric = frame.Main.Text;
        PreviousLyric = _snapshot.State != PlayerState.Stopped && index > 0 ? _lyricLines[index - 1].Text : "";
        NextLyric = _snapshot.State != PlayerState.Stopped && index + 1 < _lyricLines.Count ? _lyricLines[index + 1].Text : "";
        Translation = frame.Translation?.Text ?? "";
        SetLyricSegments(frame);
        OnPropertyChanged(nameof(CurrentLyric)); OnPropertyChanged(nameof(PreviousLyric)); OnPropertyChanged(nameof(NextLyric)); OnPropertyChanged(nameof(Translation));
    }

    private void SetLyricSegments(LyricsDisplayFrame frame)
    {
        if (!_currentLyricSegments.SequenceEqual(frame.Main.Segments))
        {
            _currentLyricSegments = frame.Main.Segments;
            OnPropertyChanged(nameof(CurrentLyricSegments));
        }
        var romanization = frame.Romanization?.Segments ?? [];
        if (!_romanizationSegments.SequenceEqual(romanization))
        {
            _romanizationSegments = romanization;
            OnPropertyChanged(nameof(RomanizationSegments));
            OnPropertyChanged(nameof(HasRomanization));
        }
        OnPropertyChanged(nameof(HasTranslation));
    }
    private void RaiseLyricsProperties() { OnPropertyChanged(nameof(LyricsText)); OnPropertyChanged(nameof(HasTimedLyrics)); }
    protected override void RefreshLocalizedProperties() { base.RefreshLocalizedProperties(); UpdateLyricsPosition(); RaiseLyricsProperties(); }
    public override async ValueTask DisposeAsync()
    {
        Playback.Changed -= OnPlaybackChanged;
        await _lifetime.CancelAsync(); await base.DisposeAsync(); await Task.WhenAll(_lyricsTasks);
        _lyricsCancellation?.Dispose(); _lifetime.Dispose();
    }
}
