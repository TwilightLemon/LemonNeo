using LemonNeo.Application.Lyrics;
using LemonNeo.Application.Playback;
using LemonNeo.Core.Lyrics;
using LemonNeo.Core.Playback;

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
///     Fetches without blocking input. Drawing stays on the editor thread so the
///     playback monitor and network continuations never move the input cursor.
/// </summary>
internal sealed class ConsoleLyricsDisplay(
    Func<PlaybackSessionSnapshot> snapshotSource,
    PlaybackLyricsService lyrics) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public LyricsFrame GetFrame()
    {
        var snapshot = snapshotSource();
        if (snapshot.State is not (PlaybackState.Playing or PlaybackState.Paused)) return LyricsFrame.Empty;
        var value = lyrics.Snapshot;
        if (value.RequestId != snapshot.RequestId || value.Status is PlaybackLyricsStatus.Empty or PlaybackLyricsStatus.Loading)
            return new LyricsFrame(new LyricsRow([new LyricsSegment("Loading lyrics…", false)]));
        return value.Document is { } document
            ? CreateFrame(document, snapshot.Timeline.Position.TotalMilliseconds)
            : new LyricsFrame(new LyricsRow([new LyricsSegment("No lyrics available", false)]));
    }

    internal static LyricsFrame CreateFrame(Lyrics value, double positionMs)
    {
        var frame = LyricsTimeline.CreateFrame(value, positionMs);

        static LyricsRow Row(LyricsDisplayRow row)
        {
            return new LyricsRow(row.Segments.Select(s => new LyricsSegment(s.Text, s.Reached)).ToArray());
        }

        return new LyricsFrame(Row(frame.Main), frame.Romanization is { } romanization ? Row(romanization) : null,
            frame.Translation is { } translation ? Row(translation) : null);
    }
}
