using System.Text;
using System.Text.Json;
using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;
using LemonNeo.Plugin.Lyricify;
using Lyricify.Lyrics.Models;
using Xunit;
using LemonLyrics = LemonNeo.Plugin.Abstractions.Lyrics;

namespace LemonNeo.Tests;

public sealed class LyricsConverterTests
{
    private static readonly ExternalMusicId SourceId = new("test", "1", "song-id");
    internal const string Yrc = "[1000,1500](1000,500,0)你(1500,1000,0)好\n[4000,1000](4000,1000,0)啊";

    [Theory]
    [InlineData(LyricsRawTypes.Yrc, Yrc)]
    [InlineData(LyricsRawTypes.Qrc, "[1000,1500]你(1000,500)好(1500,1000)\n[4000,1000]啊(4000,1000)")]
    [InlineData(LyricsRawTypes.Krc, "[1000,1500]<0,500,0>你<500,1000,0>好\n[4000,1000]<0,1000,0>啊")]
    public void PreservesAbsoluteWordTimes(LyricsRawTypes type, string raw)
    {
        var lyrics = LyricsConverter.Parse(raw, type, "test", SourceId);
        Assert.True(lyrics.IsSynchronized);
        Assert.True(lyrics.IsWordSynchronized);
        Assert.Equal("你好", lyrics.Lines![0].Text);
        Assert.Equal([new LyricsWord("你", 1000, 1500), new LyricsWord("好", 1500, 2500)], lyrics.Lines[0].Words);
        Assert.Equal(4000, lyrics.Lines[1].Words![0].StartTimeMs);
    }

    [Fact]
    public void PreservesTtmlSyllableTimes()
    {
        const string ttml = """
            <tt xmlns="http://www.w3.org/ns/ttml"><body><div>
              <p begin="00:01.000" end="00:02.500"><span begin="00:01.000" end="00:01.500">你</span><span begin="00:01.500" end="00:02.500">好</span></p>
            </div></body></tt>
            """;
        var lyrics = LyricsConverter.Parse(ttml, LyricsRawTypes.Ttml, "test", SourceId);
        Assert.Equal([new LyricsWord("你", 1000, 1500), new LyricsWord("好", 1500, 2500)], lyrics.Lines![0].Words);
    }

    [Fact]
    public void ParsesTranslationOnItsOwnTimelineAndAlignsLineRomanization()
    {
        const string translation = "[00:01.00]Hello\n[00:03.90]Ah";
        const string romanization = "[00:01.00]ni hao\n[00:04.00]a";
        var lyrics = LyricsConverter.Parse(Yrc, LyricsRawTypes.Yrc, "test", SourceId, translation, romanization);
        Assert.Equal(translation, lyrics.Translation);
        Assert.Equal(3900, lyrics.TranslationLines![1].StartTimeMs);
        Assert.Null(lyrics.TranslationLines[0].Words);
        Assert.Equal("Hello", lyrics.TranslationLines[0].Text);
        Assert.Equal([new LyricsWord("ni ", 1000, 1500), new LyricsWord("hao", 1500, 2500)], lyrics.RomanizationLines![0].Words);
        Assert.Equal("ni hao", lyrics.RomanizationLines[0].Text);
        Assert.Equal(4000, lyrics.RomanizationLines[1].StartTimeMs);
    }

    [Fact]
    public void PreservesIndependentWordTimedRomanization()
    {
        var lyrics = LyricsConverter.Parse(Yrc, LyricsRawTypes.Yrc, "test", SourceId,
            romanization: "[900,1800](900,700,0)ni (1600,1100,0)hao");
        Assert.Equal([new LyricsWord("ni ", 900, 1600), new LyricsWord("hao", 1600, 2700)],
            Assert.Single(lyrics.RomanizationLines!).Words);
    }

    [Theory]
    [InlineData("[00:01.00]ni hao ma", "ni hao ma", 1000)]
    [InlineData("[00:03.00]ni hao", "ni hao", 3000)]
    public void KeepsUnalignableRomanizationOnItsLineTimeline(string raw, string text, int start)
    {
        var lyrics = LyricsConverter.Parse(Yrc, LyricsRawTypes.Yrc, "test", SourceId, romanization: raw);
        var line = Assert.Single(lyrics.RomanizationLines!);
        Assert.False(line.IsWordSynchronized);
        Assert.Empty(line.Words);
        Assert.Equal(text, line.Text);
        Assert.Equal(start, line.StartTimeMs);
        Assert.Equal(raw, lyrics.Romanization);
        Assert.True(lyrics.IsWordSynchronized);
    }

    [Fact]
    public void RetainsPlainRomanizationWithoutInventingWordTimes()
    {
        var lyrics = LyricsConverter.Parse(Yrc, LyricsRawTypes.Yrc, "test", SourceId, romanization: "plain romanization");
        Assert.Null(lyrics.RomanizationLines);
        Assert.Equal("plain romanization", lyrics.Romanization);
    }

    [Fact]
    public void PreservesEmbeddedTranslationsPronunciationAndBackgroundTiming()
    {
        var main = new FullSyllableLineInfo(new SyllableLineInfo(
            [new SyllableInfo("你", 1000, 1500), new SyllableInfo("好", 1500, 2500)]), "Hello", "ni hao")
        {
            SubLine = new SyllableLineInfo([new SyllableInfo("oh", 1800, 2400)])
        };
        var lyrics = LyricsConverter.Convert(new LyricsData { Lines = [main] }, "", "test", SourceId);
        var line = Assert.Single(lyrics.Lines!);
        Assert.Equal("Hello", line.Translations!["zh"]);
        Assert.Equal("Hello", Assert.Single(lyrics.TranslationLines!).Text);
        Assert.Equal("ni hao", line.Romanization);
        Assert.Equal("ni hao", Assert.Single(lyrics.RomanizationLines!).Text);
        Assert.Equal(new LyricsWord("oh", 1800, 2400), Assert.Single(line.BackgroundLine!.Words!));
    }

    [Fact]
    public void FlattensGroupedSyllablesWithoutLosingTheirTiming()
    {
        var group = new FullSyllableInfo([new SyllableInfo("Hel", 1000, 1200), new SyllableInfo("lo", 1200, 1600)]);
        var lyrics = LyricsConverter.Convert(new LyricsData { Lines = [new SyllableLineInfo([group])] }, "", "test", SourceId);
        Assert.Equal([new LyricsWord("Hel", 1000, 1200), new LyricsWord("lo", 1200, 1600)], lyrics.Lines![0].Words);
    }

    [Fact]
    public void ReadsKrcWordRomanizationFromLanguageTag()
    {
        var language = System.Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "{\"version\":1,\"content\":[{\"type\":0,\"language\":0,\"lyricContent\":[[\"ni\",\"hao\"]]}," +
            "{\"type\":1,\"language\":1,\"lyricContent\":[[\"Hello\"]]}]}"));
        var lyrics = LyricsConverter.Parse($"[language:{language}]\n[1000,1500]<0,500,0>你<500,1000,0>好",
            LyricsRawTypes.Krc, "test", SourceId);
        Assert.Equal("Hello", lyrics.TranslationLines![0].Text);
        Assert.Equal([new LyricsWord("ni ", 1000, 1500), new LyricsWord("hao", 1500, 2500)], lyrics.RomanizationLines![0].Words);
    }

    [Fact]
    public void SupportsEmptySyllableLinesAndMalformedAuxiliaryText()
    {
        var lyrics = LyricsConverter.Convert(new LyricsData { Lines = [new SyllableLineInfo([])] }, "", "test", SourceId,
            translation: "<tt>", romanization: "[1,2](broken");
        Assert.False(lyrics.IsSynchronized);
        Assert.False(lyrics.IsWordSynchronized);
        Assert.Null(Assert.Single(lyrics.Lines!).StartTimeMs);
        Assert.Equal("<tt>", lyrics.Translation);
    }

    [Fact]
    public void StillSupportsLineTimedAndPlainLyrics()
    {
        var lrc = LyricsConverter.Parse("[00:01.00]Hello\n[00:02.50]World", LyricsRawTypes.Lrc, "test", SourceId);
        Assert.True(lrc.IsSynchronized);
        Assert.False(lrc.IsWordSynchronized);
        Assert.Equal(1000, lrc.Lines![0].StartTimeMs);
        var plain = new LemonLyrics("Hello", "en");
        Assert.False(plain.IsSynchronized);
        Assert.False(plain.IsWordSynchronized);
    }

    [Fact]
    public void RoundTripsOriginalTranslationAndRomanizationWordTimelines()
    {
        var lyrics = LyricsConverter.Parse(Yrc, LyricsRawTypes.Yrc, "test", SourceId,
            "[00:01.00]Hello", "[00:01.00]ni hao");
        var restored = JsonSerializer.Deserialize<LemonLyrics>(JsonSerializer.Serialize(lyrics))!;
        Assert.Equal(lyrics.Lines![0].Words, restored.Lines![0].Words);
        Assert.Equal(lyrics.TranslationLines![0], restored.TranslationLines![0]);
        Assert.Equal(lyrics.RomanizationLines![0].Words, restored.RomanizationLines![0].Words);
    }
}
