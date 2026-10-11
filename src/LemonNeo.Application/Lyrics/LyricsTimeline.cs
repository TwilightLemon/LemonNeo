using System.Text.RegularExpressions;
using LemonNeo.Core.Lyrics;
using LyricsModel = LemonNeo.Core.Lyrics.Lyrics;

namespace LemonNeo.Application.Lyrics;

public sealed record LyricsDisplaySegment(string Text, bool Reached);

public sealed record LyricsDisplayRow(IReadOnlyList<LyricsDisplaySegment> Segments)
{
    public string Text => string.Concat(Segments.Select(s => s.Text));
    public static LyricsDisplayRow Empty { get; } = new([]);
}

public sealed record LyricsDisplayFrame(
    LyricsDisplayRow Main,
    LyricsDisplayRow? Romanization,
    LyricsDisplayRow? Translation);

/// <summary>Host-independent selection of lyric lines and reached words.</summary>
public static partial class LyricsTimeline
{
    [GeneratedRegex(@"^\s*(?:\[-?\d+(?:[:,.]\d+)+\]|\[(?:ar|ti|al|by|offset):|<\?xml\b|<tt\b)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RawTimedLyrics();

    public static LyricsDisplayFrame CreateFrame(LyricsModel value, double positionMs)
    {
        var row = SelectLine(LyricsDocument.Create(value), line => line.Main.EffectiveStartTimeMs,
            line => line.Main.EffectiveEndTimeMs, positionMs);
        var mainLine = row?.Main;
        var main = mainLine is null
            ? value.Lines is { Count: > 0 } ? LyricsDisplayRow.Empty : TextRow(FirstTextLine(value.Text))
            : CreateRow(mainLine, positionMs);
        var romanized = row is null ? null : LyricDocumentRow.SelectAuxiliary(row.Romanization, positionMs);
        var romanization = romanized is not null
            ? CreateRow(romanized, positionMs)
            : null;
        var translated = row is null ? null : LyricDocumentRow.SelectAuxiliary(row.Translation, positionMs);
        var translation = translated is not null
            ? CreateRow(translated, positionMs, false)
            : null;
        return new LyricsDisplayFrame(main, string.IsNullOrWhiteSpace(romanization?.Text) ? null : romanization,
            string.IsNullOrWhiteSpace(translation?.Text) ? null : translation);
    }

    private static T? SelectLine<T>(IReadOnlyList<T>? lines, Func<T, int?> startTime, Func<T, int?> endTime,
        double positionMs) where T : class
    {
        var timed = lines?.Where(line => startTime(line) is not null).OrderBy(startTime).ToArray();
        if (timed is not { Length: > 0 }) return lines?.FirstOrDefault();
        var line = timed.LastOrDefault(item => startTime(item) <= positionMs) ?? timed[0];
        return endTime(line) is { } end && positionMs >= end ? null : line;
    }

    private static LyricsDisplayRow CreateRow(LyricsLine line, double positionMs, bool highlightLine = true)
    {
        return line.Words is { Count: > 0 } words
            ? WordRow(words, positionMs)
            : new LyricsDisplayRow([
                new LyricsDisplaySegment(line.Text, highlightLine && positionMs >= line.EffectiveStartTimeMs)
            ]);
    }

    private static LyricsDisplayRow WordRow(IReadOnlyList<LyricsWord> words, double positionMs)
    {
        return new LyricsDisplayRow(words
            .Select(word => new LyricsDisplaySegment(word.Text, positionMs >= word.StartTimeMs)).ToArray());
    }

    private static LyricsDisplayRow TextRow(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? LyricsDisplayRow.Empty
            : new LyricsDisplayRow([new LyricsDisplaySegment(text, false)]);
    }

    private static string? FirstTextLine(string? text)
    {
        return text?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    internal static string[] PlainAuxiliaryLines(string? text)
    {
        return text is null || RawTimedLyrics().IsMatch(text)
            ? []
            : text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
    }
}