using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using LemonNeo.Application.Lyrics;
using LemonNeo.Core.Lyrics;

namespace LemonNeo.Desktop.Controls.Lyrics;

/// <summary>Shapes text once, then highlights and lifts timed words, including wrapped words.</summary>
public sealed class LyricText : TextBlock
{
    public static readonly StyledProperty<bool> IsVerticalProperty =
        AvaloniaProperty.Register<LyricText, bool>(nameof(IsVertical));

    private VerticalLyricLayout? _verticalLayout;
    private bool _verticalBoundsReady;

    public bool IsVertical
    {
        get => GetValue(IsVerticalProperty);
        set => SetValue(IsVerticalProperty, value);
    }

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

    public static readonly StyledProperty<bool> EnableWordAnimationProperty =
        AvaloniaProperty.Register<LyricText, bool>(nameof(EnableWordAnimation), true);

    public static readonly StyledProperty<double> LongWordDurationMultiplierProperty =
        AvaloniaProperty.Register<LyricText, double>(nameof(LongWordDurationMultiplier),
            LyricAnimationTiming.DefaultDurationMultiplier,
            coerce: (_, value) => double.IsFinite(value) ? Math.Max(1, value) : LyricAnimationTiming.DefaultDurationMultiplier);

    public static readonly StyledProperty<int?> LineStartTimeMsProperty =
        AvaloniaProperty.Register<LyricText, int?>(nameof(LineStartTimeMs));

    private readonly LinearGradientBrush _mask = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Colors.White, 0), new GradientStop(Colors.White, 0),
            new GradientStop(Colors.Transparent, 1), new GradientStop(Colors.Transparent, 1)
        ]
    };

    private TextLayout? _highlightLayout;
    private TextLayout? _sourceLayout;
    private Rect[][] _wordBounds = [];
    private LyricAnimationTiming[]? _animationTiming;
    private Rect[][]? _animationBounds;
    private LyricWordAnimation? _wordAnimation;

    static LyricText()
    {
        AffectsRender<LyricText>(IsHighlightedProperty, ReduceMotionProperty, EnableWordAnimationProperty,
            LineStartTimeMsProperty);
        AffectsMeasure<LyricText>(IsVerticalProperty, WordsProperty, EnableWordAnimationProperty);
    }

    public IReadOnlyList<LyricsDisplaySegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public IReadOnlyList<LyricsWord>? Words
    {
        get => GetValue(WordsProperty);
        set => SetValue(WordsProperty, value);
    }

    public IBrush? ReachedBrush
    {
        get => GetValue(ReachedBrushProperty);
        set => SetValue(ReachedBrushProperty, value);
    }

    public double PositionMilliseconds
    {
        get => GetValue(PositionMillisecondsProperty);
        set => SetValue(PositionMillisecondsProperty, value);
    }

    public bool IsHighlighted
    {
        get => GetValue(IsHighlightedProperty);
        set => SetValue(IsHighlightedProperty, value);
    }

    public bool ReduceMotion
    {
        get => GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    public bool EnableWordAnimation
    {
        get => GetValue(EnableWordAnimationProperty);
        set => SetValue(EnableWordAnimationProperty, value);
    }

    public double LongWordDurationMultiplier
    {
        get => GetValue(LongWordDurationMultiplierProperty);
        set => SetValue(LongWordDurationMultiplierProperty, value);
    }

    public int? LineStartTimeMs
    {
        get => GetValue(LineStartTimeMsProperty);
        set => SetValue(LineStartTimeMsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PositionMillisecondsProperty)
        {
            if (CanAnimateWords && _wordAnimation is { } animation) animation.UpdatePosition(PositionMilliseconds);
            else
            {
                if (CanAnimateWords) InvalidateArrange();
                else ClearWordAnimation();
                InvalidateVisual();
            }
        }

        if (change.Property == IsHighlightedProperty || change.Property == ReduceMotionProperty ||
            change.Property == EnableWordAnimationProperty)
        {
            if (!CanAnimateWords) ClearWordAnimation();
            InvalidateArrange();
        }

        if (change.Property == WordsProperty || change.Property == SegmentsProperty)
        {
            _animationTiming = null;
            SetCurrentValue(TextProperty, Words is { Count: > 0 } words
                ? string.Concat(words.Select(w => w.Text))
                : Segments is { } segments
                    ? string.Concat(segments.Select(s => s.Text))
                    : Text);
            ClearHighlightLayout();
            InvalidateVisual();
        }

        if (change.Property == LongWordDurationMultiplierProperty)
        {
            _animationTiming = null;
            _animationBounds = null;
            ClearWordAnimation();
            InvalidateArrange();
            InvalidateVisual();
        }

        if (change.Property == ReachedBrushProperty || change.Property == IsVerticalProperty ||
            change.Property == TextProperty || change.Property == FontFamilyProperty ||
            change.Property == FontSizeProperty || change.Property == FontWeightProperty ||
            change.Property == FontStyleProperty || change.Property == FontStretchProperty ||
            change.Property == LetterSpacingProperty || change.Property == ForegroundProperty)
        {
            _verticalLayout?.Dispose();
            _verticalLayout = null;
            ClearHighlightLayout();
            InvalidateArrange();
            InvalidateVisual();
        }
    }

    private VerticalLyricLayout VerticalLayout => _verticalLayout ??= new VerticalLyricLayout(Text ?? "",
        new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, LetterSpacing, Foreground, ReachedBrush);

    // Reserve space even before the line becomes active, so lifting never changes its layout
    // or gets cropped by the compact desktop viewport.
    private Thickness AnimationPadding => EnableWordAnimation && Words is { Count: > 0 }
        ? new Thickness(FontSize * .18, FontSize * .32, FontSize * .18, FontSize * .18) : default;

    protected override Size MeasureOverride(Size availableSize) => (IsVertical
        ? VerticalLayout.Size : base.MeasureOverride(availableSize.Deflate(AnimationPadding))).Inflate(AnimationPadding);

    protected override Size ArrangeOverride(Size finalSize)
    {
        base.ArrangeOverride(finalSize.Deflate(AnimationPadding));
        if (CanAnimateWords)
        {
            EnsureHighlightLayout();
            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            // Supersample on low-DPI screens to keep cached text sharp during fractional translations.
            var rasterScaling = Math.Max(2, scaling);
            if (_wordAnimation?.RenderScaling != rasterScaling) ClearWordAnimation();
            if (_wordAnimation is null)
            {
                _animationTiming ??= LyricAnimationTiming.Create(Words!, LongWordDurationMultiplier);
                _animationBounds ??= _animationTiming.Select(timing => GetRangeBounds(timing.Start, timing.Length)).ToArray();
                _wordAnimation = new LyricWordAnimation(_animationTiming, _animationBounds, FontSize, rasterScaling, IsVertical,
                    context => DrawNormalLayout(context, default),
                    ReachedBrush is null ? null : context => DrawReachedLayout(context, default));
                VisualChildren.Add(_wordAnimation);
            }
            var padding = Padding;
            if (UseLayoutRounding)
                padding = new Thickness(Math.Round(padding.Left * scaling) / scaling,
                    Math.Round(padding.Top * scaling) / scaling, Math.Round(padding.Right * scaling) / scaling,
                    Math.Round(padding.Bottom * scaling) / scaling);
            var y = padding.Top + AnimationPadding.Top;
            if (!IsVertical && finalSize.Height < TextLayout.Height)
                y += VerticalAlignment switch
                {
                    Avalonia.Layout.VerticalAlignment.Center => (finalSize.Height - TextLayout.Height) / 2,
                    Avalonia.Layout.VerticalAlignment.Bottom => finalSize.Height - TextLayout.Height,
                    _ => 0
                };
            _wordAnimation.SetOrigin(IsVertical
                ? new Point(Math.Max(0, (finalSize.Width - VerticalLayout.Size.Width) / 2), AnimationPadding.Top)
                : new Point(padding.Left + AnimationPadding.Left, y));
            _wordAnimation.Measure(finalSize);
            _wordAnimation.Arrange(new Rect(finalSize));
            _wordAnimation.UpdatePosition(PositionMilliseconds);
        }
        return finalSize;
    }

    internal Rect[] GetRangeBounds(int start, int length) => IsVertical
        ? VerticalLayout.HitTestTextRange(start, length) : TextLayout.HitTestTextRange(start, length).ToArray();

    internal object LayoutIdentity => IsVertical ? VerticalLayout : TextLayout;

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        if (IsVertical)
        {
            origin = new Point(Math.Max(0, (Bounds.Width - VerticalLayout.Size.Width) / 2), AnimationPadding.Top);
        }
        else origin += new Vector(AnimationPadding.Left, AnimationPadding.Top);

        if (CanAnimateWords && _wordAnimation is not null) return;

        DrawNormalLayout(context, origin);
        RenderHighlight(context, origin);
    }

    private bool CanAnimateWords => EnableWordAnimation && IsHighlighted && !ReduceMotion &&
                                    Words is { Count: > 0 } && double.IsFinite(PositionMilliseconds);

    private void RenderHighlight(DrawingContext context, Point origin)
    {
        if ((!IsHighlighted && Segments is null) || ReachedBrush is null || string.IsNullOrEmpty(Text)) return;
        EnsureHighlightLayout();
        if (Words is { Count: > 0 } words)
        {
            for (var i = 0; i < words.Count; i++)
            {
                var word = words[i];
                var progress = PositionMilliseconds < word.StartTimeMs
                    ? 0
                    : ReduceMotion || word.EndTimeMs <= word.StartTimeMs
                        ? 1
                        : Math.Clamp((PositionMilliseconds - word.StartTimeMs) / (word.EndTimeMs - word.StartTimeMs), 0,
                            1);
                DrawHighlight(context, origin, _wordBounds[i], progress);
            }
        }
        else if (Segments is { } segments)
        {
            for (var i = 0; i < segments.Count; i++)
                if (segments[i].Reached)
                    DrawHighlight(context, origin, _wordBounds[i], 1);
        }
        else
        {
            // Line-synchronized lyrics fade as a line; they do not invent word timestamps.
            var opacity = ReduceMotion || LineStartTimeMs is null
                ? 1
                : Math.Clamp((PositionMilliseconds - LineStartTimeMs.Value) / 240, 0, 1);
            using (context.PushOpacity(opacity))
            {
                DrawReachedLayout(context, origin);
            }
        }
    }

    private void EnsureHighlightLayout()
    {
        if (IsVertical)
        {
            if (_verticalBoundsReady) return;
            _wordBounds = MeasureWordBounds();
            _verticalBoundsReady = true;
            return;
        }
        var layout = TextLayout;
        if (ReferenceEquals(layout, _sourceLayout)) return;
        ClearHighlightLayout();
        _sourceLayout = layout;
        _highlightLayout = new TextLayout(Text, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize,
            ReachedBrush, TextAlignment, TextWrapping, TextTrimming, TextDecorations, FlowDirection,
            layout.MaxWidth, layout.MaxHeight, LineHeight, LetterSpacing, MaxLines, FontFeatures);
        _wordBounds = MeasureWordBounds();
    }

    private Rect[][] MeasureWordBounds()
    {
        var texts = Words?.Select(w => w.Text) ?? Segments?.Select(s => s.Text) ?? [];
        var offset = 0;
        return texts.Select(text =>
        {
            // Keep empty fragments aligned with their timestamps without hit-testing a zero-length range.
            var bounds = text.Length == 0 ? [] : GetRangeBounds(offset, text.Length);
            offset += text.Length;
            return bounds;
        }).ToArray();
    }

    private void DrawHighlight(DrawingContext context, Point origin, Rect[] bounds, double progress)
    {
        if (progress <= 0) return;
        _mask.EndPoint = IsVertical ? new RelativePoint(0, 1, RelativeUnit.Relative) : new RelativePoint(1, 0, RelativeUnit.Relative);
        var width = bounds.Sum(b => IsVertical ? b.Height : b.Width);
        var consumed = 0d;
        foreach (var bound in bounds)
        {
            var extent = IsVertical ? bound.Height : bound.Width;
            if (extent <= 0) continue;
            var local = Math.Clamp((width * progress - consumed) / extent, 0, 1);
            consumed += extent;
            if (local <= 0) continue;
            var rectangle = bound.Translate(new Vector(origin.X, origin.Y));
            using (context.PushClip(rectangle))
            {
                if (local >= 1)
                {
                    DrawReachedLayout(context, origin);
                }
                else
                {
                    var edge = local * 1.3 - .15;
                    _mask.GradientStops[1].Offset = Math.Clamp(edge - .15, 0, 1);
                    _mask.GradientStops[2].Offset = Math.Clamp(edge + .15, 0, 1);
                    using (context.PushOpacityMask(_mask, rectangle))
                    {
                        DrawReachedLayout(context, origin);
                    }
                }
            }
        }
    }

    private void ClearHighlightLayout()
    {
        _verticalBoundsReady = false;
        _highlightLayout?.Dispose();
        _highlightLayout = null;
        _sourceLayout = null;
        _wordBounds = [];
        _animationBounds = null;
        ClearWordAnimation();
    }

    private void ClearWordAnimation()
    {
        if (_wordAnimation is not { } animation) return;
        VisualChildren.Remove(animation);
        animation.Dispose();
        _wordAnimation = null;
    }

    private void DrawNormalLayout(DrawingContext context, Point origin)
    {
        if (IsVertical) VerticalLayout.Draw(context, origin, false);
        else base.RenderTextLayout(context, origin);
    }

    private void DrawReachedLayout(DrawingContext context, Point origin)
    {
        if (IsVertical) VerticalLayout.Draw(context, origin, true);
        else _highlightLayout!.Draw(context, origin);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ClearHighlightLayout();
        _verticalLayout?.Dispose();
        _verticalLayout = null;
        base.OnDetachedFromVisualTree(e);
    }
}
