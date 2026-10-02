using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;
using System.Text.RegularExpressions;

namespace LemonNeo.ConsoleApp;

internal sealed record LyricsSegment(string Text, bool Reached);

internal sealed record LyricsRow(IReadOnlyList<LyricsSegment> Segments)
{
    public static LyricsRow Empty { get; } = new([]);
    public string Text => string.Concat(Segments.Select(segment => segment.Text));
}

internal sealed record LyricsFrame(LyricsRow Main, LyricsRow? Romanization = null, LyricsRow? Translation = null)
{
    public static LyricsFrame Empty { get; } = new(LyricsRow.Empty);

    public IReadOnlyList<LyricsRow> Rows => GetVisibleRows(3);

    public IReadOnlyList<LyricsRow> GetVisibleRows(int maximumRows)
    {
        if (maximumRows <= 0 || (Main.Segments.Count == 0 && Romanization is null && Translation is null)) return [];
        var rows = new List<LyricsRow>();
        if (maximumRows > 1 && Romanization is not null) rows.Add(Romanization);
        rows.Add(Main);
        if (rows.Count < maximumRows && Translation is not null) rows.Add(Translation);
        return rows;
    }

    public bool HasSameContent(LyricsFrame other)
    {
        var rows = Rows;
        var otherRows = other.Rows;
        return rows.Count == otherRows.Count && rows.Zip(otherRows)
            .All(pair => pair.First.Segments.SequenceEqual(pair.Second.Segments));
    }
}

/// <summary>
/// Fetches without blocking input. Drawing stays on the editor thread so the
/// playback monitor and network continuations never move the input cursor.
/// </summary>
internal sealed class ConsoleLyricsDisplay(
    Func<PlaybackSessionSnapshot> snapshotSource,
    LyricsService lyrics,
    CancellationToken cancellationToken) : IAsyncDisposable
{
    private static readonly Regex RawTimedLyrics = new(
        @"^\s*(?:\[-?\d+(?:[:,.]\d+)+\]|\[(?:ar|ti|al|by|offset):|<\?xml\b|<tt\b)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly List<Task<LyricsLookupResult>> _loads = [];
    private MusicItemId? _trackId;
    private CancellationTokenSource? _loadCancellation;
    private Task<LyricsLookupResult>? _load;

    public LyricsFrame GetFrame()
    {
        var snapshot = snapshotSource();
        var track = snapshot.State == PlayerState.Stopped ? null : snapshot.CurrentTrack;
        if (_trackId != track?.Id)
        {
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            _trackId = track?.Id;
            _load = null;
            if (track is not null)
            {
                _loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _load = LoadAsync(track, _loadCancellation.Token);
                _loads.RemoveAll(task => task.IsCompleted);
                _loads.Add(_load);
            }
            else
            {
                _loadCancellation = null;
            }
        }
        if (track is null) return LyricsFrame.Empty;
        if (_load is not { IsCompletedSuccessfully: true })
        {
            return new LyricsFrame(new LyricsRow([new LyricsSegment("Loading lyrics…", false)]));
        }
        return _load.Result.Lyrics is { } value
            ? CreateFrame(value, snapshot.Timeline.Position.TotalMilliseconds)
            : new LyricsFrame(new LyricsRow([new LyricsSegment("No lyrics available", false)]));
    }

    private async Task<LyricsLookupResult> LoadAsync(Track track, CancellationToken token)
    {
        try
        {
            return await lyrics.GetLyricsAsync(track, token);
        }
        catch (OperationCanceledException)
        {
            return new LyricsLookupResult(null, null, new Dictionary<string, string>());
        }
    }

    internal static LyricsFrame CreateFrame(Lyrics value, double positionMs)
    {
        var mainLine = SelectLine(value.Lines, line => line.EffectiveStartTimeMs, line => line.EffectiveEndTimeMs, positionMs);
        var main = mainLine is null
            ? value.Lines is { Count: > 0 } ? LyricsRow.Empty : TextRow(FirstTextLine(value.Text))
            : CreateRow(mainLine, positionMs);

        var romanizedLine = SelectLine(value.RomanizationLines, line => line.StartTimeMs, line => line.EndTimeMs, positionMs);
        var romanization = romanizedLine is not null
            ? romanizedLine.IsWordSynchronized ? WordRow(romanizedLine.Words, positionMs)
                : new LyricsRow([new LyricsSegment(romanizedLine.Text, positionMs >= romanizedLine.StartTimeMs)])
            : value.RomanizationLines is { Count: > 0 } ? null : TextRow(mainLine?.Romanization
                ?? (mainLine is not null || value.Lines is not { Count: > 0 } ? FirstPlainAuxiliaryLine(value.Romanization) : null));

        var translationLine = SelectLine(value.TranslationLines, line => line.EffectiveStartTimeMs, line => line.EffectiveEndTimeMs, positionMs);
        var translation = translationLine is not null
            ? CreateRow(translationLine, positionMs, highlightLine: false)
            : value.TranslationLines is { Count: > 0 } ? null : TextRow(
                mainLine?.Translations?.GetValueOrDefault("zh") ?? mainLine?.Translations?.Values.FirstOrDefault()
                ?? (mainLine is not null || value.Lines is not { Count: > 0 } ? FirstPlainAuxiliaryLine(value.Translation) : null));

        return new LyricsFrame(main,
            string.IsNullOrWhiteSpace(romanization?.Text) ? null : romanization,
            string.IsNullOrWhiteSpace(translation?.Text) ? null : translation);
    }

    private static T? SelectLine<T>(
        IReadOnlyList<T>? lines,
        Func<T, int?> startTime,
        Func<T, int?> endTime,
        double positionMs) where T : class
    {
        var timed = lines?.Where(line => startTime(line) is not null).OrderBy(startTime).ToArray();
        if (timed is not { Length: > 0 }) return lines?.FirstOrDefault();
        var line = timed.LastOrDefault(item => startTime(item) <= positionMs) ?? timed[0];
        return endTime(line) is { } end && positionMs >= end ? null : line;
    }

    private static LyricsRow CreateRow(LyricsLine line, double positionMs, bool highlightLine = true) =>
        line.Words is { Count: > 0 } words
            ? WordRow(words, positionMs)
            : new LyricsRow([new LyricsSegment(line.Text, highlightLine && positionMs >= line.EffectiveStartTimeMs)]);

    private static LyricsRow WordRow(IReadOnlyList<LyricsWord> words, double positionMs) =>
        new(words.Select(word => new LyricsSegment(word.Text, positionMs >= word.StartTimeMs)).ToArray());

    private static LyricsRow TextRow(string? text) =>
        string.IsNullOrWhiteSpace(text) ? LyricsRow.Empty : new([new LyricsSegment(text, false)]);

    private static string? FirstTextLine(string? text) =>
        text?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    // Encoded lyrics must use the parsed timeline. Showing their first raw line
    // here would freeze the display and expose timestamp/metadata tags.
    private static string? FirstPlainAuxiliaryLine(string? text) =>
        text is null || RawTimedLyrics.IsMatch(text) ? null : FirstTextLine(text);

    public async ValueTask DisposeAsync()
    {
        _loadCancellation?.Cancel();
        await Task.WhenAll(_loads);
        _loadCancellation?.Dispose();
    }
}
