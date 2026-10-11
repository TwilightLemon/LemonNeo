using System.Runtime.CompilerServices;
using LemonNeo.Core.Lyrics;
using LyricsModel = LemonNeo.Core.Lyrics.Lyrics;

namespace LemonNeo.Application.Lyrics;

/// <summary>One main lyric row and the auxiliary lines associated with its timeline.</summary>
public sealed record LyricDocumentRow(
    LyricsLine Main,
    IReadOnlyList<LyricsLine> Romanization,
    IReadOnlyList<LyricsLine> Translation)
{
    // Auxiliary files may contain empty end cues. They must not erase the content of a main lyric row.
    public static LyricsLine? SelectAuxiliary(IReadOnlyList<LyricsLine> lines, double positionMs)
    {
        return lines.LastOrDefault(l => HasContent(l) && l.EffectiveStartTimeMs is { } start && start <= positionMs)
               ?? lines.FirstOrDefault(HasContent);
    }

    private static bool HasContent(LyricsLine line)
    {
        return line.IsWordSynchronized
            ? line.Words!.Any(word => !string.IsNullOrWhiteSpace(word.Text))
            : !string.IsNullOrWhiteSpace(line.Text);
    }
}

/// <summary>Aligns independent lyric files to the main timeline without changing the provider's original data.</summary>
public static class LyricsDocument
{
    public const int AuxiliaryToleranceMs = 250;
    private static readonly ConditionalWeakTable<LyricsModel, IReadOnlyList<LyricDocumentRow>> Documents = new();

    public static IReadOnlyList<LyricDocumentRow> Create(LyricsModel lyrics)
    {
        return Documents.GetValue(lyrics, Build);
    }

    private static IReadOnlyList<LyricDocumentRow> Build(LyricsModel lyrics)
    {
        var main = lyrics.Lines is { Count: > 0 } lines
            ? lines.OrderBy(l => l.EffectiveStartTimeMs ?? int.MaxValue).ToArray()
            : PlainLines(lyrics.Text).Select(text => new LyricsLine(text)).ToArray();
        var timed = main.Select((line, index) => (Index: index, Start: line.EffectiveStartTimeMs))
            .Where(line => line.Start is not null).Select(line => (line.Index, Start: line.Start!.Value)).ToArray();
        var romanization =
            Associate(
                lyrics.RomanizationLines
                    ?.Select(l => new LyricsLine(l.Text, l.StartTimeMs, l.EndTimeMs, Words: l.Words)).ToArray() ?? [],
                main, timed);
        var translation = Associate(lyrics.TranslationLines ?? [], main, timed);
        var plainRomanization = LyricsTimeline.PlainAuxiliaryLines(lyrics.Romanization);
        var plainTranslation = LyricsTimeline.PlainAuxiliaryLines(lyrics.Translation);
        return main.Select((line, index) =>
        {
            var romanized = romanization[index];
            var translated = translation[index];
            if (romanized.Count == 0 && (line.Romanization ?? plainRomanization.ElementAtOrDefault(index)) is
                { Length: > 0 } romanizedText)
                romanized = [new LyricsLine(romanizedText, line.EffectiveStartTimeMs, line.EffectiveEndTimeMs)];
            if (translated.Count == 0 && (line.Translations?.GetValueOrDefault("zh") ??
                                          line.Translations?.Values.FirstOrDefault()
                                          ?? plainTranslation.ElementAtOrDefault(index)) is
                { Length: > 0 } translatedText)
                translated = [new LyricsLine(translatedText, line.EffectiveStartTimeMs, line.EffectiveEndTimeMs)];
            return new LyricDocumentRow(line, romanized, translated);
        }).ToArray();
    }

    private static IReadOnlyList<LyricsLine>[] Associate(IReadOnlyList<LyricsLine> auxiliary, LyricsLine[] main,
        (int Index, int Start)[] timed)
    {
        var result = main.Select(_ => new List<LyricsLine>()).ToArray();
        for (var index = 0; index < auxiliary.Count; index++)
        {
            var line = auxiliary[index];
            if (line.EffectiveStartTimeMs is not { } start || timed.Length == 0)
            {
                if (index < result.Length) result[index].Add(line);
                continue;
            }

            var low = 0;
            var high = timed.Length - 1;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                if (timed[middle].Start <= start) low = middle + 1;
                else high = middle - 1;
            }

            var nearest = Math.Clamp(high, 0, timed.Length - 1);
            if (low < timed.Length &&
                Math.Abs((long)timed[low].Start - start) < Math.Abs((long)timed[nearest].Start - start)) nearest = low;
            if (Math.Abs((long)timed[nearest].Start - start) <= AuxiliaryToleranceMs)
            {
                var target = timed[nearest];
                var delta = target.Start - start;
                var nextStart = nearest + 1 < timed.Length ? (int?)timed[nearest + 1].Start : null;
                result[target.Index].Add(line with
                {
                    StartTimeMs = target.Start,
                    EndTimeMs = main[target.Index].EffectiveEndTimeMs ??
                                nextStart ?? (line.EffectiveEndTimeMs is { } end ? end + delta : null),
                    Words = delta == 0
                        ? line.Words
                        : line.Words?.Select(word => word with
                        {
                            StartTimeMs = word.StartTimeMs + delta, EndTimeMs = word.EndTimeMs + delta
                        }).ToArray()
                });
            }
            else if (high >= 0)
            {
                // Multiple auxiliary lines may belong inside a longer main row. Never carry one across a main-row gap.
                var target = timed[high];
                var end = Math.Min(main[target.Index].EffectiveEndTimeMs ?? int.MaxValue,
                    high + 1 < timed.Length ? timed[high + 1].Start : int.MaxValue);
                if (start < end) result[target.Index].Add(line);
            }
        }

        return result.Select(lines => (IReadOnlyList<LyricsLine>)lines.OrderBy(l => l.EffectiveStartTimeMs).ToArray())
            .ToArray();
    }

    private static string[] PlainLines(string? text)
    {
        return text?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray() ?? [];
    }
}