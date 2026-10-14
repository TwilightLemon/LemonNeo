using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace LemonNeo.Desktop.Controls.Lyrics;

/// <summary>
/// Measures a vertical line in reading order. CJK and emoji occupy upright grapheme cells;
/// sideways script runs retain normal shaping, kerning and UTF-16 hit testing.
/// See https://www.unicode.org/reports/tr50/ for mixed-script vertical orientation.
/// </summary>
internal sealed class VerticalLyricLayout : IDisposable
{
    private readonly List<Run> _runs = [];
    public Size Size { get; }

    public VerticalLyricLayout(string text, Typeface typeface, double fontSize, double letterSpacing,
        IBrush? foreground, IBrush? highlight)
    {
        var height = 0d;
        var width = fontSize;
        foreach (var (start, length, upright, display) in SplitRuns(text))
        {
            var layout = new TextLayout(display, typeface, fontSize, foreground, letterSpacing: letterSpacing);
            var reached = new TextLayout(display, typeface, fontSize, highlight, letterSpacing: letterSpacing);
            var advance = upright ? Math.Max(1, fontSize + letterSpacing) : layout.WidthIncludingTrailingWhitespace;
            _runs.Add(new Run(start, length, upright, height, advance, layout, reached));
            height += advance;
            width = Math.Max(width, upright ? layout.WidthIncludingTrailingWhitespace : layout.Height);
        }

        Size = new Size(width, height);
    }

    internal static IEnumerable<(int Start, int Length, bool Upright, string Display)> SplitRuns(string text)
    {
        var elements = StringInfo.GetTextElementEnumerator(text);
        var sideways = new StringBuilder();
        var sidewaysStart = 0;
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var codepoint = Rune.GetRuneAt(element, 0).Value;
            var form = VerticalForm(element);
            var upright = form != element || IsUpright(codepoint) || element.Contains('\uFE0F') || element.Contains('\u20E3');
            if (!upright)
            {
                if (sideways.Length == 0) sidewaysStart = elements.ElementIndex;
                sideways.Append(element);
                continue;
            }

            if (sideways.Length > 0)
            {
                yield return (sidewaysStart, sideways.Length, false, sideways.ToString());
                sideways.Clear();
            }

            yield return (elements.ElementIndex, element.Length, true, form);
        }

        if (sideways.Length > 0) yield return (sidewaysStart, sideways.Length, false, sideways.ToString());
    }

    private static bool IsUpright(int scalar) => scalar is
        >= 0x1100 and <= 0x11FF or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7FF or
        >= 0xF900 and <= 0xFAFF or >= 0xFE10 and <= 0xFE1F or >= 0xFE30 and <= 0xFE4F or
        >= 0xFF01 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or
        >= 0x1F000 and <= 0x1FAFF or >= 0x20000 and <= 0x3FFFF
        && scalar is not (0x30FC or 0xFF0D);

    // Presentation forms also work with fallback fonts that do not expose OpenType vert/vrt2.
    private static string VerticalForm(string text) => text switch
    {
        "、" => "︑", "。" => "︒", "，" => "︐", "：" => "︓", "；" => "︔",
        "！" => "︕", "？" => "︖", "…" => "︙", "—" => "︱", "_" => "︳",
        "（" => "︵", "）" => "︶", "｛" => "︷", "｝" => "︸",
        "〔" => "︹", "〕" => "︺", "【" => "︻", "】" => "︼",
        "《" => "︽", "》" => "︾", "〈" => "︿", "〉" => "﹀",
        "「" => "﹁", "」" => "﹂", "『" => "﹃", "』" => "﹄",
        "［" => "﹇", "］" => "﹈", _ => text
    };

    public Rect[] HitTestTextRange(int start, int length)
    {
        if (length <= 0) return [];
        var rectangles = new List<Rect>();
        foreach (var run in _runs)
        {
            var from = Math.Max(start, run.Start);
            var to = Math.Min(start + length, run.Start + run.Length);
            if (from >= to) continue;
            if (run.Upright)
                rectangles.Add(new Rect(0, run.Offset, Size.Width, run.Advance));
            else
                foreach (var bound in run.Normal.HitTestTextRange(from - run.Start, to - from))
                    rectangles.Add(new Rect(0, run.Offset + bound.X, Size.Width, bound.Width));
        }

        return rectangles.ToArray();
    }

    public void Draw(DrawingContext context, Point origin, bool highlighted)
    {
        foreach (var run in _runs)
        {
            var layout = highlighted ? run.Highlight : run.Normal;
            if (run.Upright)
                layout.Draw(context, new Point(origin.X + (Size.Width - layout.WidthIncludingTrailingWhitespace) / 2,
                    origin.Y + run.Offset + (run.Advance - layout.Height) / 2));
            else
            {
                // Rotate only the shaped sideways run, never the line, viewport, or window.
                using (context.PushTransform(new Matrix(0, 1, -1, 0,
                           origin.X + (Size.Width + layout.Height) / 2, origin.Y + run.Offset)))
                    layout.Draw(context, default);
            }
        }
    }

    public void Dispose()
    {
        foreach (var run in _runs)
        {
            run.Normal.Dispose();
            run.Highlight.Dispose();
        }
    }

    private sealed record Run(int Start, int Length, bool Upright, double Offset, double Advance,
        TextLayout Normal, TextLayout Highlight);
}
