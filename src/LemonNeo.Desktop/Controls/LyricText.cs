using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using LemonNeo.Application;

namespace LemonNeo.Desktop.Controls;

/// <summary>Uses Avalonia's text layout and wrapping for individually highlighted lyric words.</summary>
public sealed class LyricText : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<LyricsDisplaySegment>?> SegmentsProperty =
        AvaloniaProperty.Register<LyricText, IReadOnlyList<LyricsDisplaySegment>?>(nameof(Segments));
    public static readonly StyledProperty<IBrush?> ReachedBrushProperty = AvaloniaProperty.Register<LyricText, IBrush?>(nameof(ReachedBrush));
    public IReadOnlyList<LyricsDisplaySegment>? Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public IBrush? ReachedBrush { get => GetValue(ReachedBrushProperty); set => SetValue(ReachedBrushProperty, value); }
    static LyricText()
    {
        SegmentsProperty.Changed.AddClassHandler<LyricText>((control, _) => control.UpdateWords());
        ReachedBrushProperty.Changed.AddClassHandler<LyricText>((control, _) => control.UpdateWords());
        ForegroundProperty.Changed.AddClassHandler<LyricText>((control, _) => control.UpdateWords());
    }
    private void UpdateWords()
    {
        Inlines ??= new InlineCollection();
        Inlines.Clear();
        foreach (var segment in Segments ?? []) Inlines.Add(new Run(segment.Text) { Foreground = segment.Reached ? ReachedBrush ?? Foreground : Foreground });
    }
}
