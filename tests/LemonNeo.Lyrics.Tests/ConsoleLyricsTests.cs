using LemonNeo.Application;
using LemonNeo.ConsoleApp;
using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;
using Xunit;

namespace LemonNeo.Tests;

public sealed class ConsoleLyricsTests
{
    private static Lyrics Sample => new("你好\n啊", IsSynchronized: true, Lines:
    [
        new LyricsLine("你好", Words: [new LyricsWord("你", 1000, 1500), new LyricsWord("好", 1500, 2500)]),
        new LyricsLine("啊", 4000, 5000, Words: [new LyricsWord("啊", 4000, 5000)])
    ]);

    [Theory]
    [InlineData(999, "你好", 0)]
    [InlineData(1000, "你好", 1)]
    [InlineData(1499, "你好", 1)]
    [InlineData(1500, "你好", 2)]
    [InlineData(2499, "你好", 2)]
    [InlineData(2500, "", 0)]
    [InlineData(4000, "啊", 1)]
    [InlineData(5000, "", 0)]
    public void HighlightsReachedWordsAtTheirStartAndClearsEndedLines(int position, string expectedText, int reached)
    {
        var frame = ConsoleLyricsDisplay.CreateFrame(Sample, position);
        Assert.Equal(expectedText, frame.Main.Text);
        Assert.Equal(reached, frame.Main.Segments.Count(segment => segment.Reached));
    }

    [Fact]
    public void RecomputesHighlightingAfterSeekingBackward()
    {
        var after = ConsoleLyricsDisplay.CreateFrame(Sample, 1800);
        var before = ConsoleLyricsDisplay.CreateFrame(Sample, 1200);
        Assert.All(after.Main.Segments, segment => Assert.True(segment.Reached));
        Assert.True(before.Main.Segments[0].Reached);
        Assert.False(before.Main.Segments[1].Reached);
    }

    [Fact]
    public void FallsBackToWholeLineTimingAndUntimedText()
    {
        var lineTimed = new Lyrics("Hello", Lines: [new LyricsLine("Hello", 1000)]);
        Assert.False(ConsoleLyricsDisplay.CreateFrame(lineTimed, 999).Main.Segments[0].Reached);
        Assert.True(ConsoleLyricsDisplay.CreateFrame(lineTimed, 1000).Main.Segments[0].Reached);
        var plain = ConsoleLyricsDisplay.CreateFrame(new Lyrics("Hello\nWorld"), 100000);
        Assert.Equal("Hello", plain.Main.Text);
        Assert.False(plain.Main.Segments[0].Reached);
    }

    [Fact]
    public void DisplaysRomanizationMainAndTranslationOnTheirOwnTimelines()
    {
        var value = Sample with
        {
            RomanizationLines = [new LyricsRomanizationLine([new LyricsWord("ni ", 900, 1700), new LyricsWord("hao", 1700, 2500)])],
            TranslationLines = [new LyricsLine("Hello", 900, 1650), new LyricsLine("Hi", 1650, 2500)]
        };
        var frame = ConsoleLyricsDisplay.CreateFrame(value, 1600);
        Assert.Equal(["ni hao", "你好", "Hello"], frame.Rows.Select(row => row.Text));
        Assert.Equal(1, frame.Romanization!.Segments.Count(segment => segment.Reached));
        Assert.Equal(2, frame.Main.Segments.Count(segment => segment.Reached));
        Assert.False(Assert.Single(frame.Translation!.Segments).Reached);

        var translated = ConsoleLyricsDisplay.CreateFrame(value, 1650);
        Assert.Equal("Hi", translated.Translation!.Text);
        Assert.Equal(frame.Main.Segments, translated.Main.Segments);
        Assert.Equal(frame.Romanization.Segments, translated.Romanization!.Segments);
        Assert.False(frame.HasSameContent(translated));
        Assert.True(translated.HasSameContent(ConsoleLyricsDisplay.CreateFrame(value, 1699)));

        var romanized = ConsoleLyricsDisplay.CreateFrame(value, 1700);
        Assert.All(romanized.Romanization!.Segments, segment => Assert.True(segment.Reached));
        Assert.False(translated.HasSameContent(romanized));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void OmitsMissingAuxiliaryRows(bool romanization, bool translation)
    {
        var value = Sample with
        {
            RomanizationLines = romanization ? [new LyricsRomanizationLine([new LyricsWord("ni hao", 1000, 2500)])] : null,
            TranslationLines = translation ? [new LyricsLine("Hello", 1000, 2500)] : null
        };
        var expected = new List<string>();
        if (romanization) expected.Add("ni hao");
        expected.Add("你好");
        if (translation) expected.Add("Hello");
        Assert.Equal(expected, ConsoleLyricsDisplay.CreateFrame(value, 1200).Rows.Select(row => row.Text));
    }

    [Fact]
    public void ClearsAuxiliaryRowsWhenTheirLinesEnd()
    {
        var value = Sample with
        {
            RomanizationLines = [new LyricsRomanizationLine([new LyricsWord("ni hao", 1000, 1600)])],
            TranslationLines = [new LyricsLine("Hello", 1000, 1400)]
        };
        Assert.Equal(3, ConsoleLyricsDisplay.CreateFrame(value, 1200).Rows.Count);
        Assert.Equal(["ni hao", "你好"], ConsoleLyricsDisplay.CreateFrame(value, 1400).Rows.Select(row => row.Text));
        Assert.Equal("你好", Assert.Single(ConsoleLyricsDisplay.CreateFrame(value, 1600).Rows).Text);
        Assert.Empty(ConsoleLyricsDisplay.CreateFrame(value, 2500).Rows);
    }

    [Fact]
    public void AlsoDisplaysAuxiliaryTextFromLegacyLineMetadata()
    {
        var value = new Lyrics("你好", Lines:
            [new LyricsLine("你好", 1000, 2500, new Dictionary<string, string> { ["zh"] = "你好呀" }, "ni hao")]);
        var frame = ConsoleLyricsDisplay.CreateFrame(value, 1200);
        Assert.Equal(["ni hao", "你好", "你好呀"], frame.Rows.Select(row => row.Text));
        Assert.False(Assert.Single(frame.Romanization!.Segments).Reached);
    }

    [Fact]
    public void DisplaysUntimedAuxiliaryTextWithPlainLyrics()
    {
        var value = new Lyrics("你好", Lines: [new LyricsLine("你好")], Translation: "Hello", Romanization: "ni hao");
        var frame = ConsoleLyricsDisplay.CreateFrame(value, 1200);
        Assert.Equal(["ni hao", "你好", "Hello"], frame.Rows.Select(row => row.Text));
        Assert.All(frame.Rows.SelectMany(row => row.Segments), segment => Assert.False(segment.Reached));
    }

    [Fact]
    public void KeepsMainLyricsVisibleWhenTheWindowCannotFitThreeRows()
    {
        var value = Sample with
        {
            RomanizationLines = [new LyricsRomanizationLine([new LyricsWord("ni hao", 1000, 2500)])],
            TranslationLines = [new LyricsLine("Hello", 1000, 2500)]
        };
        var frame = ConsoleLyricsDisplay.CreateFrame(value, 1200);
        Assert.Equal("你好", Assert.Single(frame.GetVisibleRows(1)).Text);
        Assert.Equal(["ni hao", "你好"], frame.GetVisibleRows(2).Select(row => row.Text));
    }

    [Fact]
    public void FitsChineseAndEmojiWithoutSplittingSurrogatePairs()
    {
        var frame = new LyricsRow([new LyricsSegment("你好🙂世界", false)]);
        var viewport = LyricsRowRenderer.CreateViewport(frame, 7);
        Assert.Equal("你好🙂…", string.Concat(viewport.Select(segment => segment.Text)));
        Assert.DoesNotContain("\ufffd", string.Concat(viewport.Select(segment => segment.Text)));
    }

    [Fact]
    public void ScrollsLongLinesToKeepReachedWordsVisible()
    {
        var frame = new LyricsRow([new LyricsSegment("一二三四五六七八", true), new LyricsSegment("九十", false)]);
        var viewport = LyricsRowRenderer.CreateViewport(frame, 10);
        Assert.StartsWith("…", viewport[0].Text);
        Assert.Contains("八", string.Concat(viewport.Where(segment => segment.Reached).Select(segment => segment.Text)));
        Assert.Contains("九", string.Concat(viewport.Select(segment => segment.Text)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void HandlesVeryNarrowWindows(int width)
    {
        var frame = new LyricsRow([new LyricsSegment("你好", true)]);
        var viewport = LyricsRowRenderer.CreateViewport(frame, width);
        var text = string.Concat(viewport.Select(segment => segment.Text));
        Assert.True(text.Length <= width);
    }

    [Fact]
    public void PreventsLyricControlCharactersFromMovingTheCursor()
    {
        var frame = new LyricsRow([new LyricsSegment("a\r\nb\u001b[2J", true)]);
        var viewport = LyricsRowRenderer.CreateViewport(frame, 30);
        Assert.Equal("a  b [2J", string.Concat(viewport.Select(segment => segment.Text)));
    }

    [Fact]
    public async Task CancelsPreviousFetchAndRejectsLateLyricsAfterSwitchingTracks()
    {
        var first = Track("1");
        var second = Track("2");
        var snapshot = Snapshot(first, PlayerState.Playing, 1200);
        var provider = new DelayedLyricsProvider();
        await using var display = new ConsoleLyricsDisplay(() => snapshot, new LyricsService(new Registry(provider)), default);
        Assert.Equal("Loading lyrics…", display.GetFrame().Main.Text);
        snapshot = Snapshot(second, PlayerState.Playing, 1200);
        display.GetFrame();
        Assert.True(provider.Requests["1"].Token.IsCancellationRequested);
        provider.Requests["2"].Completion.SetResult(new Lyrics("second"));
        await WaitForLoadAsync(display, "second");
        provider.Requests["1"].Completion.SetResult(new Lyrics("first"));
        Assert.Equal("second", display.GetFrame().Main.Text);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task PauseRetainsHighlightingAndStopClearsTheRow()
    {
        var track = Track("1");
        var snapshot = Snapshot(track, PlayerState.Playing, 1200);
        var provider = new DelayedLyricsProvider();
        await using var display = new ConsoleLyricsDisplay(() => snapshot, new LyricsService(new Registry(provider)), default);
        display.GetFrame();
        provider.Requests["1"].Completion.SetResult(Sample);
        await WaitForLoadAsync(display, "你好");
        snapshot = Snapshot(track, PlayerState.Paused, 1200);
        var frame = display.GetFrame();
        Assert.True(frame.Main.Segments[0].Reached);
        Assert.False(frame.Main.Segments[1].Reached);
        Assert.Single(provider.Requests);
        snapshot = Snapshot(track, PlayerState.Stopped, 0);
        Assert.Empty(display.GetFrame().Rows);
    }

    private static async Task WaitForLoadAsync(ConsoleLyricsDisplay display, string text)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (display.GetFrame().Main.Text != text) await Task.Delay(10, timeout.Token);
    }

    private static Track Track(string id) => new() { Id = new MusicItemId("test", id), Title = id };
    private static PlaybackSessionSnapshot Snapshot(Track track, PlayerState state, int position) =>
        new(state, track, new PlaybackTimelineInfo(TimeSpan.FromMilliseconds(position), null),
            PlaybackMode.Sequential, 0, 1, "console", null);

    private sealed class Registry(IMusicProvider provider) : IProviderRegistry
    {
        public IReadOnlyCollection<IMusicProvider> Providers => [provider];
        public IMusicProvider? Get(string providerId) => providerId == provider.Id ? provider : null;
    }

    private sealed class DelayedLyricsProvider : IMusicProvider, ILyricsProvider
    {
        public string Id => "test";
        public ProviderDescriptor Descriptor => new("test", "test");
        public Dictionary<string, (TaskCompletionSource<Lyrics?> Completion, CancellationToken Token)> Requests { get; } = [];
        public Task<Lyrics?> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<Lyrics?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(track.Id.RemoteId, (completion, cancellationToken));
            return completion.Task;
        }
    }
}
