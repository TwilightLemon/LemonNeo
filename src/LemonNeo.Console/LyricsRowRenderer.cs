using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace LemonNeo.ConsoleApp;

internal enum LyricsAnsiColor { None, TrueColor, Indexed }

internal static class LyricsRowRenderer
{
    internal const string Orange = "\u001b[38;2;255;165;0m";
    internal const string IndexedOrange = "\u001b[38;5;208m";
    private static readonly Lazy<bool> SupportsAnsi = new(EnableAnsi);

    public static void Render(LyricsRow frame, int maximumWidth)
    {
        if (frame.Segments.Count == 0) return;
        var color = HighlightingTextWriter.CanUseColor;
        var ansiColor = color && SupportsAnsi.Value
            ? SelectAnsiColor(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable("TERM"),
                Environment.GetEnvironmentVariable("COLORTERM"))
            : LyricsAnsiColor.None;
        if (!color || ansiColor != LyricsAnsiColor.None)
        {
            Render(frame, maximumWidth, global::System.Console.Out, ansiColor);
            return;
        }

        var original = global::System.Console.ForegroundColor;
        try
        {
            foreach (var segment in CreateViewport(frame, maximumWidth))
            {
                global::System.Console.ForegroundColor = segment.Reached ? ConsoleColor.DarkYellow : ConsoleColor.DarkGray;
                global::System.Console.Write(segment.Text);
            }
        }
        finally
        {
            global::System.Console.ForegroundColor = original;
        }
    }

    internal static LyricsAnsiColor SelectAnsiColor(bool isWindows, string? term, string? colorTerm)
    {
        if (string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase)) return LyricsAnsiColor.None;
        if (isWindows || string.Equals(colorTerm, "truecolor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(colorTerm, "24bit", StringComparison.OrdinalIgnoreCase)) return LyricsAnsiColor.TrueColor;
        // Terminal.app and other 256-color terminals need an indexed orange instead of RGB.
        return term?.Contains("256color", StringComparison.OrdinalIgnoreCase) == true
            ? LyricsAnsiColor.Indexed : LyricsAnsiColor.None;
    }

    internal static void Render(LyricsRow frame, int maximumWidth, TextWriter output, LyricsAnsiColor color)
    {
        try
        {
            foreach (var segment in CreateViewport(frame, maximumWidth))
            {
                if (color != LyricsAnsiColor.None)
                    output.Write(segment.Reached
                        ? color == LyricsAnsiColor.TrueColor ? Orange : IndexedOrange
                        : "\u001b[90m");
                output.Write(segment.Text);
            }
        }
        finally
        {
            if (color != LyricsAnsiColor.None) output.Write("\u001b[39m");
        }
    }

    internal static IReadOnlyList<LyricsSegment> CreateViewport(LyricsRow frame, int maximumWidth)
    {
        if (maximumWidth <= 0 || frame.Segments.Count == 0) return [];
        var cells = frame.Segments.SelectMany(segment => segment.Text.EnumerateRunes()
            .Select(rune => new Cell(Rune.IsControl(rune) ? " " : rune.ToString(), segment.Reached, CellWidth(rune))))
            .ToArray();
        if (cells.Length == 0) return [];

        // Keep the last reached word in view when a lyric exceeds the terminal width.
        var start = 0;
        if (cells.Sum(cell => cell.Width) > maximumWidth)
        {
            var focus = Array.FindLastIndex(cells, cell => cell.Reached);
            if (focus >= 0)
            {
                start = focus;
                var budget = Math.Max(0, maximumWidth * 2 / 3 - 1);
                var used = cells[start].Width;
                while (start > 0 && used + cells[start - 1].Width <= budget)
                {
                    used += cells[--start].Width;
                }
            }
        }

        var result = new List<LyricsSegment>();
        var width = start > 0 ? 1 : 0;
        if (start > 0) result.Add(new LyricsSegment("…", false));
        for (var index = start; index < cells.Length; index++)
        {
            var cell = cells[index];
            var reserveEllipsis = index < cells.Length - 1 ? 1 : 0;
            if (width + cell.Width + reserveEllipsis > maximumWidth)
            {
                if (width < maximumWidth) result.Add(new LyricsSegment("…", false));
                break;
            }
            result.Add(new LyricsSegment(cell.Text, cell.Reached));
            width += cell.Width;
        }
        // Combine neighboring cells to avoid emitting a color change for every character.
        var grouped = new List<LyricsSegment>();
        var text = new StringBuilder();
        bool? reached = null;
        foreach (var segment in result)
        {
            if (reached is { } previous && previous != segment.Reached)
            {
                grouped.Add(new LyricsSegment(text.ToString(), previous));
                text.Clear();
            }
            reached = segment.Reached;
            text.Append(segment.Text);
        }
        if (reached is { } last) grouped.Add(new LyricsSegment(text.ToString(), last));
        return grouped;
    }

    private static int CellWidth(Rune rune)
    {
        if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or
            UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        return rune.Value is >= 0x1100 and <= 0x115f or >= 0x2e80 and <= 0xa4cf or
            >= 0xac00 and <= 0xd7a3 or >= 0xf900 and <= 0xfaff or >= 0xfe10 and <= 0xfe6f or
            >= 0xff00 and <= 0xff60 or >= 0xffe0 and <= 0xffe6 or >= 0x1f300 and <= 0x1faff ? 2 : 1;
    }

    private static bool EnableAnsi()
    {
        if (!HighlightingTextWriter.CanUseColor) return false;
        if (!OperatingSystem.IsWindows()) return true;
        var handle = GetStdHandle(-11);
        return GetConsoleMode(handle, out var mode) && SetConsoleMode(handle, mode | 0x0004);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    private sealed record Cell(string Text, bool Reached, int Width);
}
