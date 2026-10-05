using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
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
    private UiPreferences _preferences;
    private int _pendingPreferenceSaves;
    private readonly List<Task> _preferenceTasks = [];
    public LyricsViewModel(DesktopContext context, PlaybackViewModel playback) : base(context, DesktopRoutes.Lyrics)
    {
        Playback = playback; _snapshot = playback.Snapshot;
        _preferences = context.Appearance.Current;
        AlignLeftCommand = new(() => SetAlignment(LyricAlignment.Left));
        AlignCenterCommand = new(() => SetAlignment(LyricAlignment.Center));
        SingleDesktopLineCommand = new(() => DesktopLyricLineCount = 1);
        TwoDesktopLinesCommand = new(() => DesktopLyricLineCount = 2);
        SeekLyricCommand = new(position => Playback.SeekAsync(position / 1000), position => position >= 0 && Playback.CanSeek);
        Context.Appearance.Changed += OnAppearanceChanged;
        Playback.Changed += OnPlaybackChanged;
        UpdateSnapshot();
    }
    public PlaybackViewModel Playback { get; }
    public Lyrics? Document => _lyrics;
    public double PositionMilliseconds => _snapshot.Timeline.Position.TotalMilliseconds;
    public double DurationMilliseconds => _snapshot.Timeline.Duration?.TotalMilliseconds ?? 0;
    public bool IsStopped => _snapshot.State == PlayerState.Stopped;
    public bool ReduceMotion => Context.Appearance.Current.ReduceMotion;
    public RelayCommand AlignLeftCommand { get; }
    public RelayCommand AlignCenterCommand { get; }
    public RelayCommand SingleDesktopLineCommand { get; }
    public RelayCommand TwoDesktopLinesCommand { get; }
    public bool IsSingleDesktopLine => DesktopLyricLineCount == 1;
    public bool IsTwoDesktopLines => DesktopLyricLineCount == 2;
    public int DesktopLyricLineCount
    {
        get => _preferences.DesktopLyricLineCount;
        set
        {
            var count = Math.Clamp(value, 1, 2);
            if (count == DesktopLyricLineCount) return;
            _preferences = _preferences with { DesktopLyricLineCount = count };
            RefreshPreferenceProperties();
            QueuePreferenceSave(p => p with { DesktopLyricLineCount = count });
        }
    }
    public AsyncRelayCommand<double> SeekLyricCommand { get; }
    public double DesktopLyricWindowWidth => _preferences.DesktopLyricWindowWidth;
    public double DesktopLyricWindowHeight => _preferences.DesktopLyricWindowHeight;
    public void SaveDesktopWindowSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return;
        if (width == DesktopLyricWindowWidth && height == DesktopLyricWindowHeight) return;
        _preferences = _preferences with { DesktopLyricWindowWidth = width, DesktopLyricWindowHeight = height };
        QueuePreferenceSave(p => p with { DesktopLyricWindowWidth = width, DesktopLyricWindowHeight = height });
    }
    public double DesktopLyricFontSize
    {
        get => _preferences.DesktopLyricFontSize;
        set
        {
            var size = double.IsFinite(value) ? Math.Clamp(Math.Round(value), 16, 48) : 28;
            if (size == DesktopLyricFontSize) return;
            _preferences = _preferences with { DesktopLyricFontSize = size };
            RefreshPreferenceProperties();
            QueuePreferenceSave(p => p with { DesktopLyricFontSize = size });
        }
    }
    public bool DesktopShowRomanization
    {
        get => _preferences.DesktopShowRomanization;
        set
        {
            if (value == DesktopShowRomanization) return;
            _preferences = _preferences with { DesktopShowRomanization = value };
            RefreshPreferenceProperties();
            QueuePreferenceSave(p => p with { DesktopShowRomanization = value });
        }
    }
    public bool DesktopShowTranslation
    {
        get => _preferences.DesktopShowTranslation;
        set
        {
            if (value == DesktopShowTranslation) return;
            _preferences = _preferences with { DesktopShowTranslation = value };
            RefreshPreferenceProperties();
            QueuePreferenceSave(p => p with { DesktopShowTranslation = value });
        }
    }
    public bool IsLeftAligned => _preferences.LyricAlignment == LyricAlignment.Left;
    public bool IsCenterAligned => !IsLeftAligned;
    public TextAlignment LyricTextAlignment => IsLeftAligned ? TextAlignment.Left : TextAlignment.Center;
    public double LyricFontSize
    {
        get => _preferences.LyricFontSize;
        set
        {
            var size = double.IsFinite(value) ? Math.Clamp(Math.Round(value), 16, 48) : 24;
            if (size == LyricFontSize) return;
            _preferences = _preferences with { LyricFontSize = size };
            RefreshPreferenceProperties();
            QueuePreferenceSave(p => p with { LyricFontSize = size });
        }
    }
    public bool ShowRomanization
    {
        get => _preferences.ShowRomanization;
        set
        {
            if (value == ShowRomanization) return;
            _preferences = _preferences with { ShowRomanization = value };
            RefreshPreferenceProperties();
            QueuePreferenceSave(p => p with { ShowRomanization = value });
        }
    }
    public bool ShowTranslation
    {
        get => _preferences.ShowTranslation;
        set
        {
            if (value == ShowTranslation) return;
            _preferences = _preferences with { ShowTranslation = value };
            RefreshPreferenceProperties();
            QueuePreferenceSave(p => p with { ShowTranslation = value });
        }
    }
    public bool BoldLyrics
    {
        get => _preferences.BoldLyrics;
        set
        {
            if (value == BoldLyrics) return;
            _preferences = _preferences with { BoldLyrics = value };
            RefreshPreferenceProperties();
            QueuePreferenceSave(p => p with { BoldLyrics = value });
        }
    }

    private void SetAlignment(LyricAlignment alignment)
    {
        if (_preferences.LyricAlignment == alignment) return;
        _preferences = _preferences with { LyricAlignment = alignment };
        RefreshPreferenceProperties();
        QueuePreferenceSave(p => p with { LyricAlignment = alignment });
    }
    private void QueuePreferenceSave(Func<UiPreferences, UiPreferences> update)
    {
        if (_pendingPreferenceSaves == 0) SetError(null);
        _preferenceTasks.RemoveAll(t => t.IsCompleted);
        _preferenceTasks.Add(SavePreferenceAsync(update));
    }
    private async Task SavePreferenceAsync(Func<UiPreferences, UiPreferences> update)
    {
        _pendingPreferenceSaves++;
        try { await Context.Appearance.UpdateAsync(update, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { SetError("OperationFailed", [exception.Message]); }
        finally
        {
            if (--_pendingPreferenceSaves == 0)
            {
                _preferences = Context.Appearance.Current;
                RefreshPreferenceProperties();
            }
        }
    }
    private void OnAppearanceChanged(object? sender, EventArgs args)
    {
        if (_pendingPreferenceSaves == 0) _preferences = Context.Appearance.Current;
        RefreshPreferenceProperties();
    }
    private void RefreshPreferenceProperties()
    {
        foreach (var name in new[] { nameof(LyricFontSize), nameof(ShowRomanization), nameof(ShowTranslation), nameof(BoldLyrics),
                     nameof(IsLeftAligned), nameof(IsCenterAligned), nameof(LyricTextAlignment), nameof(ReduceMotion),
                     nameof(DesktopLyricLineCount), nameof(IsSingleDesktopLine), nameof(IsTwoDesktopLines),
                     nameof(DesktopLyricFontSize), nameof(DesktopShowRomanization), nameof(DesktopShowTranslation) })
            OnPropertyChanged(name);
    }
    public string LyricsText => string.IsNullOrWhiteSpace(_lyrics?.Text) ? _text[_lyricsStatusKey] : _lyrics.Text;
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
        SeekLyricCommand.NotifyCanExecuteChanged();
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
        UpdateLyricsPosition();
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
        OnPropertyChanged(nameof(PositionMilliseconds));
        OnPropertyChanged(nameof(DurationMilliseconds));
        OnPropertyChanged(nameof(IsStopped));
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
    private void RaiseLyricsProperties() { OnPropertyChanged(nameof(Document)); OnPropertyChanged(nameof(LyricsText)); OnPropertyChanged(nameof(HasTimedLyrics)); }
    protected override void RefreshLocalizedProperties() { base.RefreshLocalizedProperties(); UpdateLyricsPosition(); RaiseLyricsProperties(); }
    public override async ValueTask DisposeAsync()
    {
        Playback.Changed -= OnPlaybackChanged;
        Context.Appearance.Changed -= OnAppearanceChanged;
        // Finish accepted preference writes before cancelling lyric requests on shutdown.
        await Task.WhenAll(_preferenceTasks);
        await _lifetime.CancelAsync(); await base.DisposeAsync(); await Task.WhenAll(_lyricsTasks);
        _lyricsCancellation?.Dispose(); _lifetime.Dispose();
    }
}
