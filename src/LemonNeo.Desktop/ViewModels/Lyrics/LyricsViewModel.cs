using LemonNeo.Abstractions.Playback;
using LemonNeo.Application.Lyrics;
using LemonNeo.Application.Playback;
using LemonNeo.Core.Lyrics;
using LemonNeo.Core.Music;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using CoreLyrics = LemonNeo.Core.Lyrics.Lyrics;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Playback;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.ViewModels.Lyrics;

public sealed class LyricsViewModel : PageViewModel
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _lyricsCancellation;
    private readonly List<Task> _lyricsTasks = [];
    private PlaybackSessionSnapshot _snapshot;
    private CoreLyrics? _lyrics;
    private Track? _lyricsTrack;
    private string _lyricsStatusKey = "NoLyrics";
    private IReadOnlyList<LyricsLine> _lyricLines = [];
    private LyricsPreferences _lyricsPreferences => Context.Settings.Get<LyricsPreferences>();
    private DesktopLyricsPreferences _desktopPreferences => Context.Settings.Get<DesktopLyricsPreferences>();
    private readonly IDisposable _lyricsSubscription;
    private readonly IDisposable _desktopSubscription;
    private readonly IDisposable _appearanceSubscription;
    public LyricsViewModel(DesktopContext context, PlaybackViewModel playback) : base(context, DesktopRoutes.Lyrics)
    {
        Playback = playback; _snapshot = playback.Snapshot;
        AlignLeftCommand = new(() => SetAlignment(LyricAlignment.Left));
        AlignCenterCommand = new(() => SetAlignment(LyricAlignment.Center));
        SingleDesktopLineCommand = new(() => DesktopLyricLineCount = 1);
        TwoDesktopLinesCommand = new(() => DesktopLyricLineCount = 2);
        SeekLyricCommand = new(position => Playback.SeekAsync(position / 1000), position => position >= 0 && Playback.CanSeek);
        _lyricsSubscription = Context.Settings.Subscribe<LyricsPreferences>(_ => RefreshLyricsPreferenceProperties());
        _desktopSubscription = Context.Settings.Subscribe<DesktopLyricsPreferences>(_ => RefreshDesktopPreferenceProperties());
        _appearanceSubscription = Context.Settings.Subscribe<AppearancePreferences>(_ => OnPropertyChanged(nameof(ReduceMotion)));
        Playback.Changed += OnPlaybackChanged;
        UpdateSnapshot();
    }
    public PlaybackViewModel Playback { get; }
    public CoreLyrics? Document => _lyrics;
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
        get => _desktopPreferences.LineCount;
        set
        {
            var count = Math.Clamp(value, 1, 2);
            if (count == DesktopLyricLineCount) return;
            ApplyDesktopPreferences(p => p with { LineCount = count });
        }
    }
    public AsyncRelayCommand<double> SeekLyricCommand { get; }
    public double DesktopLyricWindowWidth => _desktopPreferences.WindowWidth;
    public double DesktopLyricWindowHeight => _desktopPreferences.WindowHeight;
    public void SaveDesktopWindowSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return;
        if (width == DesktopLyricWindowWidth && height == DesktopLyricWindowHeight) return;
        ApplyDesktopPreferences(p => p with { WindowWidth = width, WindowHeight = height });
    }
    public double DesktopLyricFontSize
    {
        get => _desktopPreferences.FontSize;
        set
        {
            var size = double.IsFinite(value) ? Math.Clamp(Math.Round(value), 16, 48) : 28;
            if (size == DesktopLyricFontSize) return;
            ApplyDesktopPreferences(p => p with { FontSize = size });
        }
    }
    public bool DesktopShowRomanization
    {
        get => _desktopPreferences.ShowRomanization;
        set
        {
            if (value == DesktopShowRomanization) return;
            ApplyDesktopPreferences(p => p with { ShowRomanization = value });
        }
    }
    public bool DesktopShowTranslation
    {
        get => _desktopPreferences.ShowTranslation;
        set
        {
            if (value == DesktopShowTranslation) return;
            ApplyDesktopPreferences(p => p with { ShowTranslation = value });
        }
    }
    public bool IsLeftAligned => _lyricsPreferences.Alignment == LyricAlignment.Left;
    public bool IsCenterAligned => !IsLeftAligned;
    public TextAlignment LyricTextAlignment => IsLeftAligned ? TextAlignment.Left : TextAlignment.Center;
    public double LyricFontSize
    {
        get => _lyricsPreferences.FontSize;
        set
        {
            var size = double.IsFinite(value) ? Math.Clamp(Math.Round(value), 16, 48) : 24;
            if (size == LyricFontSize) return;
            ApplyLyricsPreferences(p => p with { FontSize = size });
        }
    }
    public bool ShowRomanization
    {
        get => _lyricsPreferences.ShowRomanization;
        set
        {
            if (value == ShowRomanization) return;
            ApplyLyricsPreferences(p => p with { ShowRomanization = value });
        }
    }
    public bool ShowTranslation
    {
        get => _lyricsPreferences.ShowTranslation;
        set
        {
            if (value == ShowTranslation) return;
            ApplyLyricsPreferences(p => p with { ShowTranslation = value });
        }
    }
    public bool BoldLyrics
    {
        get => _lyricsPreferences.Bold;
        set
        {
            if (value == BoldLyrics) return;
            ApplyLyricsPreferences(p => p with { Bold = value });
        }
    }

    private void SetAlignment(LyricAlignment alignment)
    {
        if (_lyricsPreferences.Alignment == alignment) return;
        ApplyLyricsPreferences(p => p with { Alignment = alignment });
    }
    private void ApplyLyricsPreferences(Func<LyricsPreferences, LyricsPreferences> update)
    {
        if (!_lifetime.IsCancellationRequested) Context.Settings.Apply(update);
    }
    private void ApplyDesktopPreferences(Func<DesktopLyricsPreferences, DesktopLyricsPreferences> update)
    {
        if (!_lifetime.IsCancellationRequested) Context.Settings.Apply(update);
    }
    private void RefreshLyricsPreferenceProperties()
    {
        foreach (var name in new[] { nameof(LyricFontSize), nameof(ShowRomanization), nameof(ShowTranslation), nameof(BoldLyrics),
                     nameof(IsLeftAligned), nameof(IsCenterAligned), nameof(LyricTextAlignment) }) OnPropertyChanged(name);
    }
    private void RefreshDesktopPreferenceProperties()
    {
        foreach (var name in new[] { nameof(DesktopLyricLineCount), nameof(IsSingleDesktopLine), nameof(IsTwoDesktopLines),
                     nameof(DesktopLyricFontSize), nameof(DesktopShowRomanization), nameof(DesktopShowTranslation),
                     nameof(DesktopLyricWindowWidth), nameof(DesktopLyricWindowHeight) }) OnPropertyChanged(name);
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
            var result = await Task.Run(() => _host.Get<LyricsService>().GetLyricsAsync(track, cancellationToken), cancellationToken);
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
        _lyricsSubscription.Dispose();
        _desktopSubscription.Dispose();
        _appearanceSubscription.Dispose();
        await _lifetime.CancelAsync(); await base.DisposeAsync(); await Task.WhenAll(_lyricsTasks);
        _lyricsCancellation?.Dispose(); _lifetime.Dispose();
    }
}
