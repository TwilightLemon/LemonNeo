using LemonNeo.Core.Lyrics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace LemonNeo.Desktop.Controls.Lyrics;

/// <summary>Clips a complete lyric line and follows its playback timing without truncating the text.</summary>
public sealed class LyricLineViewport : Decorator
{
    public static readonly StyledProperty<double> PositionMillisecondsProperty = AvaloniaProperty.Register<LyricLineViewport, double>(nameof(PositionMilliseconds));
    public static readonly StyledProperty<double> StartTimeMillisecondsProperty = AvaloniaProperty.Register<LyricLineViewport, double>(nameof(StartTimeMilliseconds));
    public static readonly StyledProperty<double> EndTimeMillisecondsProperty = AvaloniaProperty.Register<LyricLineViewport, double>(nameof(EndTimeMilliseconds));
    public static readonly StyledProperty<bool> IsActiveProperty = AvaloniaProperty.Register<LyricLineViewport, bool>(nameof(IsActive));
    public static readonly StyledProperty<IReadOnlyList<LyricsWord>?> WordsProperty = AvaloniaProperty.Register<LyricLineViewport, IReadOnlyList<LyricsWord>?>(nameof(Words));
    private readonly TranslateTransform _translation = new();
    private TextLayout? _wordLayout;
    private IReadOnlyList<LyricsWord>? _measuredWords;
    private double[] _wordWidths = [];

    public LyricLineViewport()
    {
        ClipToBounds = true;
        LayoutUpdated += (_, _) => UpdateOffset();
    }

    public double PositionMilliseconds { get => GetValue(PositionMillisecondsProperty); set => SetValue(PositionMillisecondsProperty, value); }
    public double StartTimeMilliseconds { get => GetValue(StartTimeMillisecondsProperty); set => SetValue(StartTimeMillisecondsProperty, value); }
    public double EndTimeMilliseconds { get => GetValue(EndTimeMillisecondsProperty); set => SetValue(EndTimeMillisecondsProperty, value); }
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public IReadOnlyList<LyricsWord>? Words { get => GetValue(WordsProperty); set => SetValue(WordsProperty, value); }
    public double HorizontalOffset => -_translation.X;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child) return default;
        child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        return new Size(Math.Min(child.DesiredSize.Width, availableSize.Width), child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is { } child)
        {
            child.RenderTransform = _translation;
            child.Arrange(new Rect(new Size(Math.Max(finalSize.Width, child.DesiredSize.Width), finalSize.Height)));
        }
        UpdateOffset(finalSize.Width);
        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_translation is null) return;
        if (change.Property == PositionMillisecondsProperty || change.Property == StartTimeMillisecondsProperty ||
            change.Property == EndTimeMillisecondsProperty || change.Property == IsActiveProperty || change.Property == WordsProperty)
            UpdateOffset();
    }

    private void UpdateOffset(double? availableWidth = null)
    {
        var width = availableWidth ?? Bounds.Width;
        var overflow = Math.Max(0, (Child?.DesiredSize.Width ?? 0) - width);
        var offset = 0d;
        if (IsActive && width > 0 && overflow > 0 && double.IsFinite(PositionMilliseconds))
        {
            if (Words is { Count: > 0 } words && Child is TextBlock text && text.IsArrangeValid)
            {
                MeasureWords(text.TextLayout, words);
                var edge = 0d;
                for (var i = 0; i < words.Count; i++)
                {
                    var word = words[i];
                    if (PositionMilliseconds < word.StartTimeMs) break;
                    var progress = word.EndTimeMs <= word.StartTimeMs ? 1
                        : Math.Clamp((PositionMilliseconds - word.StartTimeMs) / (word.EndTimeMs - word.StartTimeMs), 0, 1);
                    edge += _wordWidths[i] * progress;
                    if (progress < 1) break;
                }
                // Keep the word being sung near the center; retain the beginning/end at the viewport edges.
                offset = Math.Clamp(edge - width / 2, 0, overflow);
            }
            else if (EndTimeMilliseconds > StartTimeMilliseconds)
            {
                var progress = Math.Clamp((PositionMilliseconds - StartTimeMilliseconds) / (EndTimeMilliseconds - StartTimeMilliseconds), 0, 1);
                offset = overflow * progress;
            }
        }
        if (Math.Abs(_translation.X + offset) > .01) _translation.X = -offset;
    }

    private void MeasureWords(TextLayout layout, IReadOnlyList<LyricsWord> words)
    {
        if (ReferenceEquals(layout, _wordLayout) && ReferenceEquals(words, _measuredWords)) return;
        _wordLayout = layout; _measuredWords = words;
        var character = 0;
        _wordWidths = words.Select(word =>
        {
            var width = word.Text.Length == 0 ? 0 : layout.HitTestTextRange(character, word.Text.Length).Sum(rect => rect.Width);
            character += word.Text.Length;
            return width;
        }).ToArray();
    }
}
