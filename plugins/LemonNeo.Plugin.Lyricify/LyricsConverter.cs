using System.Text.RegularExpressions;
using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using LemonLyrics = LemonNeo.Plugin.Abstractions.Lyrics;
using LemonLyricsLine = LemonNeo.Plugin.Abstractions.LyricsLine;

namespace LemonNeo.Plugin.Lyricify;

internal static partial class LyricsConverter
{
    [GeneratedRegex(@"(?m)^(?<timestamps>(?:[ \t]*\[[^\]\r\n]+\])*)[ \t]*//[ \t]*(?=\r?$)", RegexOptions.CultureInvariant)]
    private static partial Regex EmptyQqTranslation();
    public static LemonLyrics Parse(
        string raw,
        LyricsRawTypes type,
        string source,
        ExternalMusicId sourceId,
        string? translation = null,
        string? romanization = null)
    {
        var data = ParseHelper.ParseLyrics(raw, type);
        var lyrics = Convert(data, raw, source, sourceId, translation, romanization);
        if (type != LyricsRawTypes.Krc) return lyrics;

        // KRC's type-0 language entries already correspond to source syllables.
        // Helper exposes this data separately from its parsed translation lines.
        try
        {
            var pronunciation = global::Lyricify.Lyrics.Parsers.KrcTranslationParser
                .GetTranslationRawFromKrc(raw)?.Content?.FirstOrDefault(content => content.Type == 0)?.LyricContent;
            if (pronunciation is null || lyrics.Lines is null) return lyrics;
            var romanizedLines = new List<LyricsRomanizationLine>();
            for (var index = 0; index < Math.Min(pronunciation.Count, lyrics.Lines.Count); index++)
            {
                if (pronunciation[index] is not { } tokens) continue;
                var words = AlignPronunciation(tokens.Select((text, i) =>
                    i < tokens.Count - 1 ? text + " " : text).ToArray(), lyrics.Lines[index]);
                if (words is not null) romanizedLines.Add(new LyricsRomanizationLine(words));
            }
            return romanizedLines.Count == 0 ? lyrics : lyrics with { RomanizationLines = romanizedLines };
        }
        catch (Exception)
        {
            // An invalid optional language tag must not discard the original lyrics.
            return lyrics;
        }
    }

    internal static LemonLyrics Convert(
        LyricsData? data,
        string raw,
        string source,
        ExternalMusicId sourceId,
        string? translation = null,
        string? romanization = null)
    {
        var lines = data?.Lines is { Count: > 0 } helperLines
            ? helperLines.Select(ConvertLine).ToArray()
            : raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(text => new LemonLyricsLine(text)).ToArray();
        var qqMusic = string.Equals(source, MusicIdNamespaces.QQMusic, StringComparison.OrdinalIgnoreCase);
        if (qqMusic) lines = lines.Select(NormalizeEmbeddedTranslations).ToArray();
        var translationLines = ParseAuxiliary(translation)?.Select(ConvertLine).ToArray()
            ?? lines.Where(line => line.Translations is { Count: > 0 }).Select(line => new LemonLyricsLine(
                line.Translations!.GetValueOrDefault("zh") ?? line.Translations!.Values.First(),
                line.EffectiveStartTimeMs, line.EffectiveEndTimeMs, line.Translations)).ToArray();
        if (qqMusic)
        {
            // Keep the empty row and its timestamp so a missing translation cannot shift later rows.
            translationLines = translationLines.Select(line => IsEmptyQqTranslation(line.Text)
                ? line with { Text = "", Words = null, Translations = null }
                : NormalizeEmbeddedTranslations(line)).ToArray();
            translation = translation is null ? null : EmptyQqTranslation().Replace(translation, "${timestamps}");
            if (string.IsNullOrWhiteSpace(translation)) translation = null;
        }

        var romanizedLines = new List<LyricsRomanizationLine>();
        foreach (var line in lines.Where(line => !string.IsNullOrWhiteSpace(line.Romanization)))
        {
            var words = AlignPronunciation(SplitPronunciation(line.Romanization!), line);
            romanizedLines.Add(words is not null ? new LyricsRomanizationLine(words)
                : new LyricsRomanizationLine([], line.Romanization, line.EffectiveStartTimeMs, line.EffectiveEndTimeMs));
        }
        foreach (var helperLine in ParseAuxiliary(romanization) ?? [])
        {
            var line = ConvertLine(helperLine);
            var words = line.Words;
            if (words is not { Count: > 0 } && line.StartTimeMs is { } start)
            {
                // LRC rounds times. Only transfer timing when the timestamp and
                // syllable count match, never by line index or estimated durations.
                var original = lines.Where(candidate => candidate.IsWordSynchronized)
                    .MinBy(candidate => Math.Abs((long)(candidate.EffectiveStartTimeMs ?? int.MinValue) - start));
                if (original?.EffectiveStartTimeMs is { } originalStart && Math.Abs((long)originalStart - start) <= 100)
                {
                    words = AlignPronunciation(SplitPronunciation(line.Text), original);
                }
            }
            if (words is { Count: > 0 }) romanizedLines.Add(new LyricsRomanizationLine(words));
            else romanizedLines.Add(new LyricsRomanizationLine([], line.Text, line.StartTimeMs, line.EndTimeMs));
        }

        return new LemonLyrics(
            string.Join(Environment.NewLine, lines.Select(line => line.Text)),
            IsSynchronized: lines.Any(line => line.EffectiveStartTimeMs is not null),
            Lines: lines,
            Source: source,
            SourceId: sourceId,
            Translation: translation,
            TranslationLines: translationLines.Length == 0 ? null : translationLines,
            RomanizationLines: romanizedLines.Count == 0 ? null : romanizedLines
                .DistinctBy(line => (line.StartTimeMs, line.Text)).OrderBy(line => line.StartTimeMs).ToArray(),
            Romanization: romanization);
    }

    private static LemonLyricsLine ConvertLine(ILineInfo line)
    {
        var words = line is SyllableLineInfo { Syllables.Count: > 0 } syllableLine
            ? syllableLine.Syllables.SelectMany(syllable => syllable is FullSyllableInfo full
                    ? full.SubItems.Select(item => new LyricsWord(item.Text, item.StartTime, item.EndTime))
                    : [new LyricsWord(syllable.Text, syllable.StartTime, syllable.EndTime)])
                .ToArray()
            : null;
        // Empty SyllableLineInfo instances throw when their timing properties are read.
        var emptySyllables = line is SyllableLineInfo { Syllables.Count: 0 };
        return new LemonLyricsLine(
            line.Text,
            emptySyllables ? null : line.StartTime,
            emptySyllables ? null : line.EndTime,
            line is IFullLineInfo fullLine ? new Dictionary<string, string>(fullLine.Translations) : null,
            line is IFullLineInfo fullInfo ? fullInfo.Pronunciation : null,
            line.SubLine?.Text,
            words,
            line.SubLine is { } background ? ConvertLine(background) : null);
    }

    private static bool IsEmptyQqTranslation(string text) => text.Trim() == "//";

    private static LemonLyricsLine NormalizeEmbeddedTranslations(LemonLyricsLine line) => line with
    {
        Translations = line.Translations?.ToDictionary(entry => entry.Key, entry => IsEmptyQqTranslation(entry.Value) ? "" : entry.Value),
        BackgroundLine = line.BackgroundLine is { } background ? NormalizeEmbeddedTranslations(background) : null
    };

    private static List<ILineInfo>? ParseAuxiliary(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            return ParseHelper.ParseLyrics(raw)?.Lines is { Count: > 0 } lines ? lines : null;
        }
        catch (Exception)
        {
            // Preserve raw auxiliary text even if its timing cannot be parsed.
            return null;
        }
    }

    private static string[] SplitPronunciation(string text) =>
        Regex.Matches(text, @"\S+\s*").Select(match => match.Value).ToArray();

    private static IReadOnlyList<LyricsWord>? AlignPronunciation(IReadOnlyList<string> tokens, LemonLyricsLine line)
    {
        var originalWords = line.Words?.Where(word => !string.IsNullOrWhiteSpace(word.Text)).ToArray();
        if (originalWords is not { Length: > 0 } || originalWords.Length != tokens.Count) return null;
        return tokens.Select((text, index) => new LyricsWord(
            text, originalWords[index].StartTimeMs, originalWords[index].EndTimeMs)).ToArray();
    }
}
