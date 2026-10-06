using LemonNeo.Abstractions.Playback;
using LemonNeo.Application.Lyrics;
using LemonNeo.Application.Playback;
using LemonNeo.Core.Lyrics;
using LemonNeo.Core.Music;

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
        var frame = LyricsTimeline.CreateFrame(value, positionMs);
        static LyricsRow Row(LyricsDisplayRow row) => new(row.Segments.Select(s => new LyricsSegment(s.Text, s.Reached)).ToArray());
        return new LyricsFrame(Row(frame.Main), frame.Romanization is { } romanization ? Row(romanization) : null,
            frame.Translation is { } translation ? Row(translation) : null);
    }

    public async ValueTask DisposeAsync()
    {
        _loadCancellation?.Cancel();
        await Task.WhenAll(_loads);
        _loadCancellation?.Dispose();
    }
}
