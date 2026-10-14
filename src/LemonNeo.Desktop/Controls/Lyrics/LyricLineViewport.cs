using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LemonNeo.Core.Lyrics;

namespace LemonNeo.Desktop.Controls.Lyrics;

/// <summary>Clips a complete lyric line and follows its playback timing without truncating the text.</summary>
public sealed class LyricLineViewport : Decorator
{
    public static readonly StyledProperty<bool> IsVerticalProperty =
        AvaloniaProperty.Register<LyricLineViewport, bool>(nameof(IsVertical));

    static LyricLineViewport()
    {
        AffectsMeasure<LyricLineViewport>(IsVerticalProperty);
    }

    public bool IsVertical
    {
        get => GetValue(IsVerticalProperty);
        set => SetValue(IsVerticalProperty, value);
    }

    public static readonly StyledProperty<double> PositionMillisecondsProperty =
        AvaloniaProperty.Register<LyricLineViewport, double>(nameof(PositionMilliseconds));

    public static readonly StyledProperty<double> StartTimeMillisecondsProperty =
        AvaloniaProperty.Register<LyricLineViewport, double>(nameof(StartTimeMilliseconds));

    public static readonly StyledProperty<double> EndTimeMillisecondsProperty =
        AvaloniaProperty.Register<LyricLineViewport, double>(nameof(EndTimeMilliseconds));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<LyricLineViewport, bool>(nameof(IsActive));

    public static readonly StyledProperty<IReadOnlyList<LyricsWord>?> WordsProperty =
        AvaloniaProperty.Register<LyricLineViewport, IReadOnlyList<LyricsWord>?>(nameof(Words));

    private readonly TranslateTransform _translation = new();
    private IReadOnlyList<LyricsWord>? _measuredWords;
    private object? _wordLayout;
    private double[] _wordWidths = [];

    public LyricLineViewport()
    {
        ClipToBounds = true;
        LayoutUpdated += (_, _) => UpdateOffset();
    }

    public double PositionMilliseconds
    {
        get => GetValue(PositionMillisecondsProperty);
        set => SetValue(PositionMillisecondsProperty, value);
    }

    public double StartTimeMilliseconds
    {
        get => GetValue(StartTimeMillisecondsProperty);
        set => SetValue(StartTimeMillisecondsProperty, value);
    }

    public double EndTimeMilliseconds
    {
        get => GetValue(EndTimeMillisecondsProperty);
        set => SetValue(EndTimeMillisecondsProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public IReadOnlyList<LyricsWord>? Words
    {
        get => GetValue(WordsProperty);
        set => SetValue(WordsProperty, value);
    }

    public double HorizontalOffset => -_translation.X;
    public double VerticalOffset => -_translation.Y;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child) return default;
        child.Measure(IsVertical ? new Size(availableSize.Width, double.PositiveInfinity)
            : new Size(double.PositiveInfinity, availableSize.Height));
        return IsVertical ? new Size(child.DesiredSize.Width, Math.Min(child.DesiredSize.Height, availableSize.Height))
            : new Size(Math.Min(child.DesiredSize.Width, availableSize.Width), child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is { } child)
        {
            child.RenderTransform = _translation;
            child.Arrange(new Rect(IsVertical
                ? new Size(finalSize.Width, Math.Max(finalSize.Height, child.DesiredSize.Height))
                : new Size(Math.Max(finalSize.Width, child.DesiredSize.Width), finalSize.Height)));
        }

        UpdateOffset(IsVertical ? finalSize.Height : finalSize.Width);
        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_translation is null) return;
        if (change.Property == PositionMillisecondsProperty || change.Property == StartTimeMillisecondsProperty ||
            change.Property == EndTimeMillisecondsProperty || change.Property == IsActiveProperty ||
            change.Property == WordsProperty || change.Property == IsVerticalProperty)
        {
            if (change.Property == IsVerticalProperty) _wordLayout = null;
            UpdateOffset();
        }
    }

    private void UpdateOffset(double? availableWidth = null)
    {
        var width = availableWidth ?? (IsVertical ? Bounds.Height : Bounds.Width);
        var overflow = Math.Max(0, (IsVertical ? Child?.DesiredSize.Height ?? 0 : Child?.DesiredSize.Width ?? 0) - width);
        var offset = 0d;
        if (IsActive && width > 0 && overflow > 0 && double.IsFinite(PositionMilliseconds))
        {
            if (Words is { Count: > 0 } words && Child is TextBlock text && text.IsArrangeValid)
            {
                MeasureWords(text, words);
                var edge = 0d;
                for (var i = 0; i < words.Count; i++)
                {
                    var word = words[i];
                    if (PositionMilliseconds < word.StartTimeMs) break;
                    var progress = word.EndTimeMs <= word.StartTimeMs
                        ? 1
                        : Math.Clamp((PositionMilliseconds - word.StartTimeMs) / (word.EndTimeMs - word.StartTimeMs), 0,
                            1);
                    edge += _wordWidths[i] * progress;
                    if (progress < 1) break;
                }

                // Keep the word being sung near the center; retain the beginning/end at the viewport edges.
                offset = Math.Clamp(edge - width / 2, 0, overflow);
            }
            else if (EndTimeMilliseconds > StartTimeMilliseconds)
            {
                var progress =
                    Math.Clamp(
                        (PositionMilliseconds - StartTimeMilliseconds) / (EndTimeMilliseconds - StartTimeMilliseconds),
                        0, 1);
                offset = overflow * progress;
            }
        }

        var x = IsVertical ? 0 : -offset;
        var y = IsVertical ? -offset : 0;
        if (Math.Abs(_translation.X - x) > .01) _translation.X = x;
        if (Math.Abs(_translation.Y - y) > .01) _translation.Y = y;
    }

    private void MeasureWords(TextBlock text, IReadOnlyList<LyricsWord> words)
    {
        var layout = text is LyricText lyric ? lyric.LayoutIdentity : text.TextLayout;
        if (ReferenceEquals(layout, _wordLayout) && ReferenceEquals(words, _measuredWords)) return;
        _wordLayout = layout;
        _measuredWords = words;
        var character = 0;
        _wordWidths = words.Select(word =>
        {
            var width = word.Text.Length == 0
                ? 0
                : (text is LyricText lyricText ? lyricText.GetRangeBounds(character, word.Text.Length)
                    : text.TextLayout.HitTestTextRange(character, word.Text.Length))
                    .Sum(rect => IsVertical ? rect.Height : rect.Width);
            character += word.Text.Length;
            return width;
        }).ToArray();
    }
}
