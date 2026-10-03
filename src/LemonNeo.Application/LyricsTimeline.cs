using System.Text.RegularExpressions;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Application;

public sealed record LyricsDisplaySegment(string Text, bool Reached);
public sealed record LyricsDisplayRow(IReadOnlyList<LyricsDisplaySegment> Segments)
{
    public string Text => string.Concat(Segments.Select(s => s.Text));
    public static LyricsDisplayRow Empty { get; } = new([]);
}
public sealed record LyricsDisplayFrame(LyricsDisplayRow Main, LyricsDisplayRow? Romanization, LyricsDisplayRow? Translation);

/// <summary>Host-independent selection of lyric lines and reached words.</summary>
public static partial class LyricsTimeline
{
    [GeneratedRegex(@"^\s*(?:\[-?\d+(?:[:,.]\d+)+\]|\[(?:ar|ti|al|by|offset):|<\?xml\b|<tt\b)", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RawTimedLyrics();

    public static LyricsDisplayFrame CreateFrame(Lyrics value, double positionMs)
    {
        var mainLine = SelectLine(value.Lines, line => line.EffectiveStartTimeMs, line => line.EffectiveEndTimeMs, positionMs);
        var main = mainLine is null
            ? value.Lines is { Count: > 0 } ? LyricsDisplayRow.Empty : TextRow(FirstTextLine(value.Text))
            : CreateRow(mainLine, positionMs);
        var romanized = SelectLine(value.RomanizationLines, line => line.StartTimeMs, line => line.EndTimeMs, positionMs);
        var romanization = romanized is not null
            ? romanized.IsWordSynchronized ? WordRow(romanized.Words, positionMs)
                : new LyricsDisplayRow([new(romanized.Text, positionMs >= romanized.StartTimeMs)])
            : value.RomanizationLines is { Count: > 0 } ? null : TextRow(mainLine?.Romanization
                ?? (mainLine is not null || value.Lines is not { Count: > 0 } ? FirstPlainAuxiliaryLine(value.Romanization) : null));
        var translated = SelectLine(value.TranslationLines, line => line.EffectiveStartTimeMs, line => line.EffectiveEndTimeMs, positionMs);
        var translation = translated is not null
            ? CreateRow(translated, positionMs, highlightLine: false)
            : value.TranslationLines is { Count: > 0 } ? null : TextRow(
                mainLine?.Translations?.GetValueOrDefault("zh") ?? mainLine?.Translations?.Values.FirstOrDefault()
                ?? (mainLine is not null || value.Lines is not { Count: > 0 } ? FirstPlainAuxiliaryLine(value.Translation) : null));
        return new(main, string.IsNullOrWhiteSpace(romanization?.Text) ? null : romanization,
            string.IsNullOrWhiteSpace(translation?.Text) ? null : translation);
    }

    private static T? SelectLine<T>(IReadOnlyList<T>? lines, Func<T, int?> startTime, Func<T, int?> endTime, double positionMs) where T : class
    {
        var timed = lines?.Where(line => startTime(line) is not null).OrderBy(startTime).ToArray();
        if (timed is not { Length: > 0 }) return lines?.FirstOrDefault();
        var line = timed.LastOrDefault(item => startTime(item) <= positionMs) ?? timed[0];
        return endTime(line) is { } end && positionMs >= end ? null : line;
    }
    private static LyricsDisplayRow CreateRow(LyricsLine line, double positionMs, bool highlightLine = true) =>
        line.Words is { Count: > 0 } words ? WordRow(words, positionMs)
            : new([new(line.Text, highlightLine && positionMs >= line.EffectiveStartTimeMs)]);
    private static LyricsDisplayRow WordRow(IReadOnlyList<LyricsWord> words, double positionMs) =>
        new(words.Select(word => new LyricsDisplaySegment(word.Text, positionMs >= word.StartTimeMs)).ToArray());
    private static LyricsDisplayRow TextRow(string? text) => string.IsNullOrWhiteSpace(text) ? LyricsDisplayRow.Empty : new([new(text, false)]);
    private static string? FirstTextLine(string? text) => text?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    private static string? FirstPlainAuxiliaryLine(string? text) => text is null || RawTimedLyrics().IsMatch(text) ? null : FirstTextLine(text);
}
