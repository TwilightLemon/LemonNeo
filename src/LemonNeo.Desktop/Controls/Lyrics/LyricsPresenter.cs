using LemonNeo.Application.Lyrics;
using LemonNeo.Core.Lyrics;
using System.Diagnostics;
using System.Windows.Input;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using CoreLyrics = LemonNeo.Core.Lyrics.Lyrics;

namespace LemonNeo.Desktop.Controls.Lyrics;

/// <summary>A scrollable lyric timeline with a playback clock and rendering-synchronized animations.</summary>
public sealed class LyricsPresenter : UserControl
{
    public static readonly StyledProperty<CoreLyrics?> DocumentProperty = AvaloniaProperty.Register<LyricsPresenter, CoreLyrics?>(nameof(Document));
    public static readonly StyledProperty<double> PositionMillisecondsProperty = AvaloniaProperty.Register<LyricsPresenter, double>(nameof(PositionMilliseconds));
    public static readonly StyledProperty<double> DurationMillisecondsProperty = AvaloniaProperty.Register<LyricsPresenter, double>(nameof(DurationMilliseconds));
    public static readonly StyledProperty<bool> IsPlayingProperty = AvaloniaProperty.Register<LyricsPresenter, bool>(nameof(IsPlaying));
    public static readonly StyledProperty<bool> IsStoppedProperty = AvaloniaProperty.Register<LyricsPresenter, bool>(nameof(IsStopped), true);
    public static readonly StyledProperty<bool> ShowRomanizationProperty = AvaloniaProperty.Register<LyricsPresenter, bool>(nameof(ShowRomanization), true);
    public static readonly StyledProperty<bool> ShowTranslationProperty = AvaloniaProperty.Register<LyricsPresenter, bool>(nameof(ShowTranslation), true);
    public static readonly StyledProperty<bool> BoldLyricsProperty = AvaloniaProperty.Register<LyricsPresenter, bool>(nameof(BoldLyrics));
    public static readonly StyledProperty<bool> ReduceMotionProperty = AvaloniaProperty.Register<LyricsPresenter, bool>(nameof(ReduceMotion));
    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty = AvaloniaProperty.Register<LyricsPresenter, TextAlignment>(nameof(TextAlignment), TextAlignment.Center);
    public static readonly StyledProperty<IBrush?> ReachedBrushProperty = AvaloniaProperty.Register<LyricsPresenter, IBrush?>(nameof(ReachedBrush));
    public static readonly StyledProperty<IBrush?> UnreachedBrushProperty = AvaloniaProperty.Register<LyricsPresenter, IBrush?>(nameof(UnreachedBrush));
    public static readonly StyledProperty<ICommand?> SeekCommandProperty = AvaloniaProperty.Register<LyricsPresenter, ICommand?>(nameof(SeekCommand));
    public static readonly StyledProperty<bool> CanSeekProperty = AvaloniaProperty.Register<LyricsPresenter, bool>(nameof(CanSeek));
    public static readonly StyledProperty<string?> FollowLabelProperty = AvaloniaProperty.Register<LyricsPresenter, string?>(nameof(FollowLabel));
    public static readonly StyledProperty<string?> EmptyTextProperty = AvaloniaProperty.Register<LyricsPresenter, string?>(nameof(EmptyText));
    // Zero keeps the full timeline; one/two show only the current and upcoming desktop rows.
    public static readonly StyledProperty<int> VisibleLineCountProperty = AvaloniaProperty.Register<LyricsPresenter, int>(nameof(VisibleLineCount), 0, coerce: (_, value) => Math.Clamp(value, 0, 2));

    private readonly StackPanel _lines = new() { Name = "LyricsLines", Spacing = 32, Margin = new Thickness(12, 0) };
    private readonly ScrollViewer _scroll;
    private readonly Border _topSpace = new();
    private readonly Border _bottomSpace = new();
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
    private readonly Button _follow = new() { Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 12), IsVisible = false };
    private readonly List<RowControls> _rows = [];
    private readonly Stopwatch _positionClock = new();
    private readonly Stopwatch _scrollClock = new();
    private TopLevel? _animationRoot;
    private Action<TimeSpan>? _animationFrameCallback;
    private bool _animationFramePending;
    private int _animationFrameGeneration;
    private readonly List<Visual> _visibilityAncestors = [];
    private (int Index, int Start)[] _timedRows = [];
    private int _currentIndex = -1;
    private bool _attached;
    private bool _isFollowing = true;
    private double _scrollFrom;
    private double _scrollTo;
    private bool _centerPending;
    private readonly Cursor _seekCursor = new(StandardCursorType.Hand);
    private ICommand? _observedSeekCommand;

    public LyricsPresenter()
    {
        _scroll = new ScrollViewer { Name = "LyricsScrollViewer", Content = _lines,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        // Fade only the viewport edges so the current line remains legible in both themes.
        _scroll.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = [new(Colors.Transparent, 0), new(Colors.White, .10), new(Colors.White, .88), new(Colors.Transparent, 1)]
        };
        Content = new Grid { Children = { _scroll, _empty, _follow } };
        _follow.Click += (_, _) => FollowCurrentLine();
        _scroll.AddHandler(PointerWheelChangedEvent, (_, _) => SuspendFollowing(), RoutingStrategies.Tunnel);
        _scroll.AddHandler(PointerPressedEvent, (_, _) => SuspendFollowing(), RoutingStrategies.Tunnel);
        _scroll.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Space) SuspendFollowing();
        }, RoutingStrategies.Tunnel);
        _scroll.ScrollChanged += (_, e) =>
        {
            if (e.ViewportDelta != default || e.ExtentDelta != default) RequestCenter();
        };
        LayoutUpdated += (_, _) =>
        {
            UpdateSpacers();
            if (_centerPending && _rows.All(r => r.Panel.IsArrangeValid))
            {
                _centerPending = false;
                CenterCurrentLine(false);
            }
        };
    }

    public CoreLyrics? Document { get => GetValue(DocumentProperty); set => SetValue(DocumentProperty, value); }
    public double PositionMilliseconds { get => GetValue(PositionMillisecondsProperty); set => SetValue(PositionMillisecondsProperty, value); }
    public double DurationMilliseconds { get => GetValue(DurationMillisecondsProperty); set => SetValue(DurationMillisecondsProperty, value); }
    public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public bool IsStopped { get => GetValue(IsStoppedProperty); set => SetValue(IsStoppedProperty, value); }
    public bool ShowRomanization { get => GetValue(ShowRomanizationProperty); set => SetValue(ShowRomanizationProperty, value); }
    public bool ShowTranslation { get => GetValue(ShowTranslationProperty); set => SetValue(ShowTranslationProperty, value); }
    public bool BoldLyrics { get => GetValue(BoldLyricsProperty); set => SetValue(BoldLyricsProperty, value); }
    public bool ReduceMotion { get => GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }
    public TextAlignment TextAlignment { get => GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }
    public IBrush? ReachedBrush { get => GetValue(ReachedBrushProperty); set => SetValue(ReachedBrushProperty, value); }
    public IBrush? UnreachedBrush { get => GetValue(UnreachedBrushProperty); set => SetValue(UnreachedBrushProperty, value); }
    public ICommand? SeekCommand { get => GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }
    public bool CanSeek { get => GetValue(CanSeekProperty); set => SetValue(CanSeekProperty, value); }
    public string? FollowLabel { get => GetValue(FollowLabelProperty); set => SetValue(FollowLabelProperty, value); }
    public string? EmptyText { get => GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }
    public int VisibleLineCount { get => GetValue(VisibleLineCountProperty); set => SetValue(VisibleLineCountProperty, value); }
    private bool IsCompact => VisibleLineCount > 0;
    public int CurrentIndex => _currentIndex;
    public bool IsFollowing => _isFollowing;
    public double CompactContentHeight => _currentIndex < 0 ? _empty.DesiredSize.Height
        : _rows.Where(row => row.Panel.IsVisible).Sum(row => row.Panel.DesiredSize.Height) + Math.Max(0, Math.Min(VisibleLineCount, _rows.Count - _currentIndex) - 1) * _lines.Spacing;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // UserControl initialization also changes properties before our child controls exist.
        if (_scroll is null) return;
        if (change.Property == DocumentProperty) RebuildRows();
        if (change.Property == VisibleLineCountProperty)
        {
            _scroll.OpacityMask = IsCompact ? null : new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = [new(Colors.Transparent, 0), new(Colors.White, .10), new(Colors.White, .88), new(Colors.Transparent, 1)]
            };
            _scroll.VerticalScrollBarVisibility = IsCompact ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
            _scroll.VerticalAlignment = IsCompact ? VerticalAlignment.Center : VerticalAlignment.Stretch;
            _lines.Spacing = IsCompact ? 10 : 32;
            _lines.Margin = new Thickness(IsCompact ? 4 : 12, 0);
            _empty.TextWrapping = IsCompact ? TextWrapping.NoWrap : TextWrapping.Wrap;
            _empty.TextTrimming = IsCompact ? TextTrimming.CharacterEllipsis : TextTrimming.None;
            RebuildRows();
        }
        if (change.Property == DurationMillisecondsProperty) UpdatePosition(PositionMilliseconds, false);
        if (change.Property == PositionMillisecondsProperty || change.Property == IsPlayingProperty || change.Property == IsStoppedProperty)
        {
            var expected = (double)(change.Property == PositionMillisecondsProperty ? change.OldValue! : PositionMilliseconds) + _positionClock.Elapsed.TotalMilliseconds;
            var seeking = change.Property == PositionMillisecondsProperty && Math.Abs(PositionMilliseconds - expected) > 1500;
            _positionClock.Restart();
            if (seeking) { _isFollowing = true; _follow.IsVisible = false; }
            UpdatePosition(PositionMilliseconds, !seeking);
            if (seeking) CenterCurrentLine(false);
            RequestAnimationFrame();
        }
        if (change.Property == FontSizeProperty || change.Property == ForegroundProperty || change.Property == ReachedBrushProperty || change.Property == UnreachedBrushProperty ||
            change.Property == TextAlignmentProperty || change.Property == ShowRomanizationProperty || change.Property == ShowTranslationProperty ||
            change.Property == BoldLyricsProperty || change.Property == ReduceMotionProperty)
        {
            UpdateAppearance();
            RequestCenter();
            if (ReduceMotion) { _scrollClock.Reset(); CenterCurrentLine(false); }
            RequestAnimationFrame();
        }
        if (change.Property == FollowLabelProperty) { _follow.Content = FollowLabel; AutomationProperties.SetName(_follow, FollowLabel); }
        if (change.Property == EmptyTextProperty) _empty.Text = EmptyText;
        if (change.Property == SeekCommandProperty) ObserveSeekCommand();
        if (change.Property == CanSeekProperty || change.Property == SeekCommandProperty) UpdateSeekTargets();
        if (change.Property == IsVisibleProperty) RequestAnimationFrame();
    }

    private void RebuildRows()
    {
        _scrollClock.Reset(); _rows.Clear(); _lines.Children.Clear(); _currentIndex = -1;
        _isFollowing = true; _follow.IsVisible = false;
        var document = Document is { } lyrics ? LyricsDocument.Create(lyrics) : [];
        _lines.Children.Add(_topSpace);
        foreach (var (row, index) in document.Select((row, index) => (row, index)))
        {
            var panel = new StackPanel { Spacing = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
            var main = new LyricText { Text = row.Main.Text, Words = row.Main.Words, LineStartTimeMs = row.Main.EffectiveStartTimeMs };
            var romanization = new LyricText();
            var translation = new TextBlock();
            foreach (var text in new TextBlock[] { main, romanization, translation })
            {
                text.TextWrapping = IsCompact ? TextWrapping.NoWrap : TextWrapping.Wrap;
                text.TextTrimming = TextTrimming.None;
            }
            Control Wrap(TextBlock text) => IsCompact ? new LyricLineViewport { Child = text } : text;
            panel.Children.Add(Wrap(romanization)); panel.Children.Add(Wrap(main)); panel.Children.Add(Wrap(translation));
            var controls = new RowControls(row, panel, main, romanization, translation)
                { NextStartTimeMs = document.ElementAtOrDefault(index + 1)?.Main.EffectiveStartTimeMs };
            panel.Tapped += async (_, e) =>
            {
                if (!CanSeekToRow(controls, out var position)) return;
                e.Handled = true;
                await SeekToLineAsync(position);
            };
            panel.KeyDown += async (_, e) =>
            {
                if (e.Key is not (Key.Enter or Key.Space) || !CanSeekToRow(controls, out var position)) return;
                e.Handled = true;
                await SeekToLineAsync(position);
            };
            AutomationProperties.SetName(panel, row.Main.Text);
            _rows.Add(controls); _lines.Children.Add(panel);
            UpdateAuxiliary(controls, row.Main.EffectiveStartTimeMs ?? 0);
        }
        _lines.Children.Add(_bottomSpace);
        _timedRows = _rows.Select((r, i) => (Index: i, Start: r.Source.Main.EffectiveStartTimeMs))
            .Where(r => r.Start is not null).Select(r => (r.Index, r.Start!.Value)).ToArray();
        _empty.IsVisible = _rows.Count == 0;
        _scroll.IsVisible = _rows.Count > 0;
        _scroll.Offset = default;
        UpdateAppearance(); UpdateSeekTargets(); UpdateSpacers(); UpdatePosition(PositionMilliseconds, false); RequestCenter(); RequestAnimationFrame();
    }

    private void UpdateAppearance()
    {
        _empty.FontSize = FontSize * .8; _empty.Foreground = Foreground; _empty.TextAlignment = TextAlignment;
        for (var index = 0; index < _rows.Count; index++)
        {
            var row = _rows[index];
            row.Main.FontSize = FontSize;
            row.Romanization.FontSize = FontSize * .72;
            row.Translation.FontSize = FontSize * .78;
            row.Main.FontWeight = row.Romanization.FontWeight = row.Translation.FontWeight = BoldLyrics ? FontWeight.Bold : FontWeight.Normal;
            row.Main.TextAlignment = row.Romanization.TextAlignment = row.Translation.TextAlignment = TextAlignment;
            row.Main.Foreground = row.Romanization.Foreground = UnreachedBrush ?? Foreground;
            row.Translation.Foreground = Foreground;
            row.Main.ReachedBrush = row.Romanization.ReachedBrush = ReachedBrush;
            row.Main.ReduceMotion = row.Romanization.ReduceMotion = ReduceMotion;
            row.Panel.Transitions = ReduceMotion ? null : new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(240) } };
            UpdateVisibility(row);
            row.Panel.Opacity = _timedRows.Length == 0 || index == _currentIndex ? 1 : .62;
        }
        UpdateCompactRows();
    }

    private void UpdatePosition(double position, bool animate)
    {
        var low = 0; var high = _timedRows.Length - 1; var selected = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (_timedRows[middle].Start <= position) { selected = middle; low = middle + 1; }
            else high = middle - 1;
        }
        var index = IsStopped ? -1 : _timedRows.Length > 0 ? _timedRows[Math.Max(0, selected)].Index
            : IsCompact && _rows.Count > 0 ? 0 : -1;
        if (index != _currentIndex)
        {
            if (_currentIndex >= 0)
            {
                var previous = _rows[_currentIndex];
                previous.Main.IsHighlighted = previous.Romanization.IsHighlighted = false;
                foreach (var viewport in previous.Panel.Children.OfType<LyricLineViewport>()) viewport.IsActive = false;
                previous.Panel.Opacity = .62;
            }
            _currentIndex = index;
            if (index >= 0) { _rows[index].Panel.Opacity = 1; CenterCurrentLine(animate); }
            UpdateCompactRows();
        }
        if (index < 0) return;
        var row = _rows[index];
        row.Main.PositionMilliseconds = row.Romanization.PositionMilliseconds = position;
        // Keep the completed line highlighted through any gap until the next main line begins.
        row.Main.IsHighlighted = selected >= 0;
        UpdateAuxiliary(row, position);
        UpdateHorizontalPosition(row.Main, row.Source.Main, row, position);
    }

    private void UpdateCompactRows()
    {
        if (!IsCompact) return;
        _scrollClock.Reset(); _scroll.Offset = default; _follow.IsVisible = false;
        _empty.IsVisible = _currentIndex < 0;
        _scroll.IsVisible = _currentIndex >= 0;
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            row.Panel.IsVisible = _currentIndex >= 0 && i >= _currentIndex && i < _currentIndex + VisibleLineCount;
            if (row.Panel.IsVisible && i != _currentIndex)
            {
                row.Main.IsHighlighted = row.Romanization.IsHighlighted = false;
                // Keep next-row auxiliary text aligned with that row, even before its first timestamp.
                UpdateAuxiliary(row, row.Source.Main.EffectiveStartTimeMs ?? 0);
                UpdateHorizontalPosition(row.Main, row.Source.Main, row, position: 0);
            }
        }
    }

    private void UpdateAuxiliary(RowControls row, double position)
    {
        var romanization = LyricDocumentRow.SelectAuxiliary(row.Source.Romanization, position);
        if (!ReferenceEquals(romanization, row.CurrentRomanization))
        {
            row.CurrentRomanization = romanization;
            row.Romanization.Words = romanization?.Words;
            row.Romanization.Text = romanization?.IsWordSynchronized == true ? string.Concat(romanization.Words!.Select(w => w.Text)) : romanization?.Text;
            row.Romanization.LineStartTimeMs = romanization?.EffectiveStartTimeMs;
        }
        row.Romanization.IsHighlighted = row.Main.IsHighlighted && romanization?.EffectiveStartTimeMs is { } start && start <= position;
        row.Translation.Text = LyricDocumentRow.SelectAuxiliary(row.Source.Translation, position)?.Text;
        UpdateHorizontalPosition(row.Romanization, romanization, row, position);
        UpdateHorizontalPosition(row.Translation, LyricDocumentRow.SelectAuxiliary(row.Source.Translation, position), row, position);
        UpdateVisibility(row);
    }

    private void UpdateHorizontalPosition(TextBlock text, LyricsLine? source, RowControls row, double position)
    {
        if (text.Parent is not LyricLineViewport viewport) return;
        var start = source?.EffectiveStartTimeMs ?? row.Source.Main.EffectiveStartTimeMs ?? 0;
        var end = source?.EffectiveEndTimeMs ?? row.Source.Main.EffectiveEndTimeMs ?? row.NextStartTimeMs ?? DurationMilliseconds;
        if (end <= start) end = row.NextStartTimeMs is { } next && next > start ? next
            : DurationMilliseconds > start ? DurationMilliseconds : start + Math.Max(5000, (text.Text?.Length ?? 0) * 200);
        viewport.StartTimeMilliseconds = start;
        viewport.EndTimeMilliseconds = end;
        viewport.Words = source?.Words;
        viewport.PositionMilliseconds = position;
        viewport.IsActive = !IsStopped && ReferenceEquals(_rows.ElementAtOrDefault(_currentIndex), row);
    }

    private void UpdateVisibility(RowControls row)
    {
        row.Romanization.IsVisible = ShowRomanization && !string.IsNullOrWhiteSpace(row.Romanization.Text);
        row.Translation.IsVisible = ShowTranslation && !string.IsNullOrWhiteSpace(row.Translation.Text);
        if (row.Romanization.Parent is LyricLineViewport romanization) romanization.IsVisible = row.Romanization.IsVisible;
        if (row.Translation.Parent is LyricLineViewport translation) translation.IsVisible = row.Translation.IsVisible;
    }

    private void UpdateSpacers()
    {
        var space = !IsCompact && _timedRows.Length > 0 ? Math.Max(0, _scroll.Viewport.Height * .45 - 32) : 0;
        if (Math.Abs(_topSpace.Height - space) < .5) return;
        _topSpace.Height = _bottomSpace.Height = space;
    }

    private void RequestCenter() { if (!IsCompact && _isFollowing && _timedRows.Length > 0) _centerPending = true; }

    private void CenterCurrentLine(bool animate)
    {
        if (IsCompact || !_isFollowing || _currentIndex < 0 || _scroll.Viewport.Height <= 0) return;
        var row = _rows[_currentIndex].Panel;
        if (!row.IsArrangeValid) { RequestCenter(); return; }
        var target = Math.Clamp(row.Bounds.Y + row.Bounds.Height / 2 - _scroll.Viewport.Height * .45, 0, Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height));
        if (!animate || ReduceMotion || !_attached)
        {
            _scrollClock.Reset(); _scroll.Offset = new Vector(0, target); return;
        }
        if (Math.Abs(target - _scroll.Offset.Y) < 1) return;
        _scrollFrom = _scroll.Offset.Y; _scrollTo = target; _scrollClock.Restart(); RequestAnimationFrame();
    }

    private void SuspendFollowing()
    {
        if (IsCompact || _timedRows.Length == 0 || IsStopped) return;
        _isFollowing = false; _centerPending = false; _scrollClock.Reset(); _follow.IsVisible = true;
    }

    public void FollowCurrentLine()
    {
        _isFollowing = true; _follow.IsVisible = false; CenterCurrentLine(true);
    }

    private bool CanSeekToRow(RowControls row, out double position)
    {
        position = Math.Max(0, row.Source.Main.EffectiveStartTimeMs ?? 0);
        return !IsCompact && CanSeek && row.Source.Main.EffectiveStartTimeMs is not null && SeekCommand?.CanExecute(position) == true;
    }

    private void UpdateSeekTargets()
    {
        foreach (var row in _rows)
        {
            var enabled = CanSeekToRow(row, out _);
            row.Panel.Cursor = enabled ? _seekCursor : Cursor.Default;
            row.Panel.Focusable = enabled;
        }
    }

    private async Task SeekToLineAsync(double position)
    {
        if (SeekCommand is IAsyncRelayCommand asynchronous) await asynchronous.ExecuteAsync(position);
        else SeekCommand?.Execute(position);
        FollowCurrentLine();
    }

    private void ObserveSeekCommand()
    {
        var command = _attached ? SeekCommand : null;
        if (ReferenceEquals(command, _observedSeekCommand)) return;
        if (_observedSeekCommand is not null) _observedSeekCommand.CanExecuteChanged -= OnSeekCanExecuteChanged;
        _observedSeekCommand = command;
        if (command is not null) command.CanExecuteChanged += OnSeekCanExecuteChanged;
    }

    private void OnSeekCanExecuteChanged(object? sender, EventArgs e) => UpdateSeekTargets();

    private bool NeedsAnimationFrame => _attached && IsEffectivelyVisible &&
        _animationRoot is not Window { WindowState: WindowState.Minimized } &&
        (IsPlaying && !IsStopped && (_timedRows.Length > 0 || IsCompact) && (!ReduceMotion || IsCompact)
         || !ReduceMotion && _scrollClock.IsRunning);

    private void OnAnimationFrame(int generation)
    {
        // A queued callback can arrive after this control moves to another window.
        if (generation != _animationFrameGeneration) return;
        _animationFramePending = false;
        if (!NeedsAnimationFrame) return;
        if (IsPlaying && !IsStopped) UpdatePosition(PositionMilliseconds + _positionClock.Elapsed.TotalMilliseconds, true);
        if (_scrollClock.IsRunning)
        {
            var progress = Math.Clamp(_scrollClock.Elapsed.TotalMilliseconds / 480, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            _scroll.Offset = new Vector(0, _scrollFrom + (_scrollTo - _scrollFrom) * eased);
            if (progress >= 1) _scrollClock.Reset();
        }
        RequestAnimationFrame();
    }

    private void RequestAnimationFrame()
    {
        if (_animationFramePending || !NeedsAnimationFrame || _animationRoot is null || _animationFrameCallback is null) return;
        // Share Avalonia's frame cadence, including high-refresh displays, without queuing duplicate callbacks.
        _animationFramePending = true;
        _animationRoot.RequestAnimationFrame(_animationFrameCallback);
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == IsVisibleProperty || change.Property == Window.WindowStateProperty) RequestAnimationFrame();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _animationRoot = TopLevel.GetTopLevel(this);
        var generation = ++_animationFrameGeneration;
        _animationFrameCallback = _ => OnAnimationFrame(generation);
        foreach (var ancestor in this.GetVisualAncestors())
        {
            _visibilityAncestors.Add(ancestor);
            ancestor.PropertyChanged += OnAncestorPropertyChanged;
        }
        ObserveSeekCommand(); UpdateSeekTargets(); _positionClock.Restart(); UpdatePosition(PositionMilliseconds, false); RequestCenter(); RequestAnimationFrame();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        ++_animationFrameGeneration;
        _animationFramePending = false;
        _animationFrameCallback = null;
        _animationRoot = null;
        foreach (var ancestor in _visibilityAncestors) ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        _visibilityAncestors.Clear();
        ObserveSeekCommand(); _positionClock.Reset(); _scrollClock.Reset(); base.OnDetachedFromVisualTree(e);
    }

    private sealed record RowControls(LyricDocumentRow Source, StackPanel Panel, LyricText Main, LyricText Romanization, TextBlock Translation)
    {
        public LyricsLine? CurrentRomanization { get; set; }
        public int? NextStartTimeMs { get; init; }
    }
}
