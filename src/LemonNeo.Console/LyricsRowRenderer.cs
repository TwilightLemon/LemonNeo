using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace LemonNeo.ConsoleApp;

internal static class LyricsRowRenderer
{
    internal const string Orange = "\u001b[38;2;255;165;0m";
    private static readonly bool SupportsAnsi = EnableAnsi();

    public static void Render(LyricsRow frame, int maximumWidth)
    {
        if (frame.Segments.Count == 0) return;
        var original = global::System.Console.ForegroundColor;
        var color = HighlightingTextWriter.CanUseColor;
        try
        {
            foreach (var segment in CreateViewport(frame, maximumWidth))
            {
                if (color)
                {
                    if (SupportsAnsi)
                    {
                        global::System.Console.Write(segment.Reached ? Orange : "\u001b[90m");
                    }
                    else
                    {
                        global::System.Console.ForegroundColor = segment.Reached ? ConsoleColor.DarkYellow : ConsoleColor.DarkGray;
                    }
                }
                global::System.Console.Write(segment.Text);
            }
        }
        finally
        {
            if (color)
            {
                if (SupportsAnsi) global::System.Console.Write("\u001b[39m");
                global::System.Console.ForegroundColor = original;
            }
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
