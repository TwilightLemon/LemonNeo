using LemonNeo.Application.Lyrics;
using LemonNeo.Core.Lyrics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace LemonNeo.Desktop.Controls;

/// <summary>Shapes text once, then sweeps a soft highlight across each timed word, including wrapped words.</summary>
public sealed class LyricText : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<LyricsDisplaySegment>?> SegmentsProperty =
        AvaloniaProperty.Register<LyricText, IReadOnlyList<LyricsDisplaySegment>?>(nameof(Segments));
    public static readonly StyledProperty<IReadOnlyList<LyricsWord>?> WordsProperty =
        AvaloniaProperty.Register<LyricText, IReadOnlyList<LyricsWord>?>(nameof(Words));
    public static readonly StyledProperty<IBrush?> ReachedBrushProperty =
        AvaloniaProperty.Register<LyricText, IBrush?>(nameof(ReachedBrush));
    public static readonly StyledProperty<double> PositionMillisecondsProperty =
        AvaloniaProperty.Register<LyricText, double>(nameof(PositionMilliseconds));
    public static readonly StyledProperty<bool> IsHighlightedProperty =
        AvaloniaProperty.Register<LyricText, bool>(nameof(IsHighlighted));
    public static readonly StyledProperty<bool> ReduceMotionProperty =
        AvaloniaProperty.Register<LyricText, bool>(nameof(ReduceMotion));
    public static readonly StyledProperty<int?> LineStartTimeMsProperty =
        AvaloniaProperty.Register<LyricText, int?>(nameof(LineStartTimeMs));

    private TextLayout? _highlightLayout;
    private TextLayout? _sourceLayout;
    private Rect[][] _wordBounds = [];
    private readonly LinearGradientBrush _mask = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = [new(Colors.White, 0), new(Colors.White, 0), new(Colors.Transparent, 1), new(Colors.Transparent, 1)]
    };

    public IReadOnlyList<LyricsDisplaySegment>? Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public IReadOnlyList<LyricsWord>? Words { get => GetValue(WordsProperty); set => SetValue(WordsProperty, value); }
    public IBrush? ReachedBrush { get => GetValue(ReachedBrushProperty); set => SetValue(ReachedBrushProperty, value); }
    public double PositionMilliseconds { get => GetValue(PositionMillisecondsProperty); set => SetValue(PositionMillisecondsProperty, value); }
    public bool IsHighlighted { get => GetValue(IsHighlightedProperty); set => SetValue(IsHighlightedProperty, value); }
    public bool ReduceMotion { get => GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }
    public int? LineStartTimeMs { get => GetValue(LineStartTimeMsProperty); set => SetValue(LineStartTimeMsProperty, value); }

    static LyricText() => AffectsRender<LyricText>(PositionMillisecondsProperty, IsHighlightedProperty, ReduceMotionProperty, LineStartTimeMsProperty);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WordsProperty || change.Property == SegmentsProperty)
        {
            SetCurrentValue(TextProperty, Words is { Count: > 0 } words
                ? string.Concat(words.Select(w => w.Text)) : Segments is { } segments
                    ? string.Concat(segments.Select(s => s.Text)) : Text);
            ClearHighlightLayout();
            InvalidateVisual();
        }
        if (change.Property == ReachedBrushProperty) { ClearHighlightLayout(); InvalidateVisual(); }
    }

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        base.RenderTextLayout(context, origin);
        if ((!IsHighlighted && Segments is null) || ReachedBrush is null || string.IsNullOrEmpty(Text)) return;
        EnsureHighlightLayout();
        if (Words is { Count: > 0 } words)
        {
            for (var i = 0; i < words.Count; i++)
            {
                var word = words[i];
                var progress = PositionMilliseconds < word.StartTimeMs ? 0 : ReduceMotion || word.EndTimeMs <= word.StartTimeMs
                    ? 1 : Math.Clamp((PositionMilliseconds - word.StartTimeMs) / (word.EndTimeMs - word.StartTimeMs), 0, 1);
                DrawHighlight(context, origin, _wordBounds[i], progress);
            }
        }
        else if (Segments is { } segments)
        {
            for (var i = 0; i < segments.Count; i++)
                if (segments[i].Reached) DrawHighlight(context, origin, _wordBounds[i], 1);
        }
        else
        {
            // Line-synchronized lyrics fade as a line; they do not invent word timestamps.
            var opacity = ReduceMotion || LineStartTimeMs is null ? 1
                : Math.Clamp((PositionMilliseconds - LineStartTimeMs.Value) / 240, 0, 1);
            using (context.PushOpacity(opacity)) _highlightLayout!.Draw(context, origin);
        }
    }

    private void EnsureHighlightLayout()
    {
        var layout = TextLayout;
        if (ReferenceEquals(layout, _sourceLayout)) return;
        ClearHighlightLayout();
        _sourceLayout = layout;
        _highlightLayout = new TextLayout(Text, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize,
            ReachedBrush, TextAlignment, TextWrapping, TextTrimming, TextDecorations, FlowDirection,
            layout.MaxWidth, layout.MaxHeight, LineHeight, LetterSpacing, MaxLines, FontFeatures);
        var texts = Words?.Select(w => w.Text) ?? Segments?.Select(s => s.Text) ?? [];
        var offset = 0;
        _wordBounds = texts.Select(text =>
        {
            // Keep empty fragments aligned with their timestamps without hit-testing a zero-length range.
            var bounds = text.Length == 0 ? [] : layout.HitTestTextRange(offset, text.Length).ToArray();
            offset += text.Length;
            return bounds;
        }).ToArray();
    }

    private void DrawHighlight(DrawingContext context, Point origin, Rect[] bounds, double progress)
    {
        if (progress <= 0) return;
        var width = bounds.Sum(b => b.Width);
        var consumed = 0d;
        foreach (var bound in bounds)
        {
            if (bound.Width <= 0) continue;
            var local = Math.Clamp((width * progress - consumed) / bound.Width, 0, 1);
            consumed += bound.Width;
            if (local <= 0) continue;
            var rectangle = bound.Translate(new Vector(origin.X, origin.Y));
            using (context.PushClip(rectangle))
            {
                if (local >= 1) _highlightLayout!.Draw(context, origin);
                else
                {
                    var edge = local * 1.3 - .15;
                    _mask.GradientStops[1].Offset = Math.Clamp(edge - .15, 0, 1);
                    _mask.GradientStops[2].Offset = Math.Clamp(edge + .15, 0, 1);
                    using (context.PushOpacityMask(_mask, rectangle)) _highlightLayout!.Draw(context, origin);
                }
            }
        }
    }

    private void ClearHighlightLayout()
    {
        _highlightLayout?.Dispose();
        _highlightLayout = null;
        _sourceLayout = null;
        _wordBounds = [];
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ClearHighlightLayout();
        base.OnDetachedFromVisualTree(e);
    }
}
