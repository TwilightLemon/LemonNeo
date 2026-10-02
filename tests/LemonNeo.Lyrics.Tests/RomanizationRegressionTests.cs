using LemonNeo.ConsoleApp;
using LemonNeo.Core;
using LemonNeo.Plugin.Lyricify;
using Lyricify.Lyrics.Models;
using Xunit;
using QqApi = Lyricify.Lyrics.Providers.Web.QQMusic.Api;

namespace LemonNeo.Tests;

public sealed class RomanizationRegressionTests
{
    private static readonly ExternalMusicId SourceId = new("test", "1", "song-id");

    [Fact]
    public void PreservesQqContentromaThroughResponseConversionAndDisplay()
    {
        const string response = """
            <!--
            <root><miniversion="1" /><lyric>
              <content><![CDATA[[1000,1000]あ(1000,500)い(1500,500)
            [3000,1000]う(3000,500)え(3500,500)]]></content>
              <contentts><![CDATA[[00:01.000]first
            [00:03.000]second]]></contentts>
              <contentroma><![CDATA[[1100,1000]a (1100,500)i(1600,500)
            [3100,1000]u (3100,500)e(3600,500)]]></contentroma>
            </lyric></root>
            -->
            """;
        var result = QqApi.ParseLyricsResponse(response)!;
        Assert.Contains("a (1100,500)", result.Romaji);
        var value = LyricsConverter.Parse(result.Lyrics!, LyricsRawTypes.Qrc, "qqmusic", SourceId, result.Trans, result.Romaji);
        Assert.Equal(2, value.RomanizationLines!.Count);
        Assert.All(value.RomanizationLines, line => Assert.True(line.IsWordSynchronized));
        var first = ConsoleLyricsDisplay.CreateFrame(value, 1200);
        var second = ConsoleLyricsDisplay.CreateFrame(value, 3700);
        Assert.Equal(["a i", "あい", "first"], first.Rows.Select(row => row.Text));
        Assert.Equal(["u e", "うえ", "second"], second.Rows.Select(row => row.Text));
        Assert.True(first.Romanization!.Segments[0].Reached);
        Assert.False(first.Romanization.Segments[1].Reached);
        Assert.All(second.Romanization!.Segments, segment => Assert.True(segment.Reached));
    }

    [Fact]
    public void ParsesQqRomanizationWithoutOtherLyrics()
    {
        var result = QqApi.ParseLyricsResponse("<root><contentroma><![CDATA[[00:01.000]a i]]></contentroma></root>");
        Assert.NotNull(result);
        Assert.Equal("[00:01.000]a i", result.Romaji);
    }

    [Fact]
    public void IgnoresInvalidOptionalQqRomanizationWithoutLosingOriginal()
    {
        var result = QqApi.ParseLyricsResponse("<root><content><![CDATA[[00:01.000]あい]]></content><contentroma>invalid</contentroma></root>")!;
        Assert.Equal("[00:01.000]あい", result.Lyrics);
        Assert.True(string.IsNullOrEmpty(result.Romaji));
    }

    [Fact]
    public void RefreshesNeteaseLineRomanizationWithoutYrcAndStripsTimestampTags()
    {
        // The reported track returns LRC and Romalrc, with no Yrc/Yromalrc.
        // These millisecond timestamps exercise the same response shape.
        const string original = "[00:00.851]あい\n[00:06.650]うえ\n[00:12.340]お";
        const string romanization = "[00:00.851]a i\n[00:06.650]u e\n[00:12.340]o";
        var value = LyricsConverter.Parse(original, LyricsRawTypes.Lrc, "netease", SourceId, romanization: romanization);
        Assert.Equal(3, value.RomanizationLines!.Count);
        Assert.All(value.RomanizationLines, line => Assert.False(line.IsWordSynchronized));

        var first = ConsoleLyricsDisplay.CreateFrame(value, 1000);
        var second = ConsoleLyricsDisplay.CreateFrame(value, 7000);
        var third = ConsoleLyricsDisplay.CreateFrame(value, 13000);
        Assert.Equal("a i", first.Romanization!.Text);
        Assert.Equal("u e", second.Romanization!.Text);
        Assert.Equal("o", third.Romanization!.Text);
        Assert.Equal("うえ", second.Main.Text);
        Assert.False(first.HasSameContent(second));
        Assert.DoesNotContain("[", first.Romanization.Text);
        Assert.Equal(first.Romanization.Segments, ConsoleLyricsDisplay.CreateFrame(value, 1000).Romanization!.Segments);
        Assert.Equal("a i", ConsoleLyricsDisplay.CreateFrame(value, 1000).Romanization!.Text);
    }

    [Fact]
    public void DisplaysEveryRomanizationLineWhenOnlySomeCanUseWordTiming()
    {
        const string original = "[1000,1000](1000,500,0)あ(1500,500,0)い\n[3000,1000](3000,1000,0)うえ";
        const string romanization = "[00:01.000]a i\n[00:03.000]u e";
        var value = LyricsConverter.Parse(original, LyricsRawTypes.Yrc, "netease", SourceId, romanization: romanization);
        Assert.True(value.RomanizationLines![0].IsWordSynchronized);
        Assert.False(value.RomanizationLines[1].IsWordSynchronized);
        Assert.Equal("u e", ConsoleLyricsDisplay.CreateFrame(value, 3500).Romanization!.Text);
    }

    [Fact]
    public void RoundTripsLineRomanizationTiming()
    {
        var value = LyricsConverter.Parse("[00:01.000]あい", LyricsRawTypes.Lrc, "netease", SourceId,
            romanization: "[00:01.000]a i");
        var restored = System.Text.Json.JsonSerializer.Deserialize<LemonNeo.Plugin.Abstractions.Lyrics>(
            System.Text.Json.JsonSerializer.Serialize(value))!;
        Assert.Equal(1000, restored.RomanizationLines![0].StartTimeMs);
        Assert.False(restored.RomanizationLines[0].IsWordSynchronized);
        Assert.Equal("a i", ConsoleLyricsDisplay.CreateFrame(restored, 1200).Romanization!.Text);
    }

    [Fact]
    public void NeverDisplaysUnparsedTimestampTextAsAStaticAuxiliaryLine()
    {
        var value = new LemonNeo.Plugin.Abstractions.Lyrics("あい",
            Romanization: "[00:00.851]a i\n[00:06.650]u e", Translation: "[00:00.851]first\n[00:06.650]second");
        var frame = ConsoleLyricsDisplay.CreateFrame(value, 7000);
        Assert.Null(frame.Romanization);
        Assert.Null(frame.Translation);
        Assert.Equal("あい", Assert.Single(frame.Rows).Text);
    }
}
