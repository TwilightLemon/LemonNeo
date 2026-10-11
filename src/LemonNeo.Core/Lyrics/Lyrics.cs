using LemonNeo.Core.Music;

namespace LemonNeo.Core.Lyrics;

/// <summary>
///     A word, character, or syllable. Times are absolute milliseconds from the start
///     of the track; EndTimeMs is exclusive. Text includes any intervening spaces.
/// </summary>
public sealed record LyricsWord(string Text, int StartTimeMs, int EndTimeMs);

public sealed record LyricsLine(
    string Text,
    int? StartTimeMs = null,
    int? EndTimeMs = null,
    IReadOnlyDictionary<string, string>? Translations = null,
    string? Romanization = null,
    string? BackgroundText = null,
    IReadOnlyList<LyricsWord>? Words = null,
    LyricsLine? BackgroundLine = null)
{
    public bool IsWordSynchronized => Words is { Count: > 0 };
    public int? EffectiveStartTimeMs => StartTimeMs ?? Words?.MinBy(word => word.StartTimeMs)?.StartTimeMs;
    public int? EffectiveEndTimeMs => EndTimeMs ?? Words?.MaxBy(word => word.EndTimeMs)?.EndTimeMs;
}

/// <summary>
///     Romanization preserves word timing when available. Line timing and text are
///     retained as a fallback when the provider does not supply word timestamps.
/// </summary>
public sealed record LyricsRomanizationLine(
    IReadOnlyList<LyricsWord> Words,
    string? LineText = null,
    int? LineStartTimeMs = null,
    int? LineEndTimeMs = null)
{
    public bool IsWordSynchronized => Words.Count > 0;
    public string Text => LineText ?? string.Concat(Words.Select(word => word.Text));
    public int? StartTimeMs => LineStartTimeMs ?? Words.MinBy(word => word.StartTimeMs)?.StartTimeMs;
    public int? EndTimeMs => LineEndTimeMs ?? Words.MaxBy(word => word.EndTimeMs)?.EndTimeMs;
}

public sealed record Lyrics(
    string Text,
    string? Language = null,
    bool IsSynchronized = false,
    IReadOnlyList<LyricsLine>? Lines = null,
    string? Source = null,
    ExternalMusicId? SourceId = null,
    string? Translation = null,
    IReadOnlyList<LyricsLine>? TranslationLines = null,
    IReadOnlyList<LyricsRomanizationLine>? RomanizationLines = null,
    string? Romanization = null)
{
    public bool IsWordSynchronized => Lines?.Any(line => line.IsWordSynchronized) == true;
}