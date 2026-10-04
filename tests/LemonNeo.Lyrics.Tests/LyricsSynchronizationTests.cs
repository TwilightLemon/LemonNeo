using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;
using LemonNeo.Plugin.Lyricify;
using Lyricify.Lyrics.Models;
using Xunit;
using LemonLyrics = LemonNeo.Plugin.Abstractions.Lyrics;

namespace LemonNeo.Lyrics.Tests;

public sealed class LyricsSynchronizationTests
{
    [Theory]
    [InlineData(-250)]
    [InlineData(-200)]
    [InlineData(200)]
    [InlineData(250)]
    public void AuxiliaryOffsetsFollowTheMainTimelineAndPreserveWordDurations(int offset)
    {
        var words = new LyricsWord[] { new("first ", 1000 + offset, 1600 + offset), new("second", 1600 + offset, 2400 + offset) };
        var lyrics = new LemonLyrics("First\nSecond", Lines: [new("First", 1000, 3000), new("Second", 3000, 5000)],
            TranslationLines: [new("第一句", 1000 + offset), new("第二句", 3000 + offset)],
            RomanizationLines: [new(words), new([], "last", 3000 + offset)]);
        var first = LyricsTimeline.CreateFrame(lyrics, 1000);
        Assert.Equal("第一句", first.Translation!.Text);
        Assert.Equal("first second", first.Romanization!.Text);
        Assert.True(first.Romanization.Segments[0].Reached); Assert.False(first.Romanization.Segments[1].Reached);
        Assert.Equal("第一句", LyricsTimeline.CreateFrame(lyrics, 2999).Translation!.Text);
        Assert.Equal("第二句", LyricsTimeline.CreateFrame(lyrics, 3000).Translation!.Text);
        Assert.Equal("last", LyricsTimeline.CreateFrame(lyrics, 3000).Romanization!.Text);
        var aligned = Assert.Single(LyricsDocument.Create(lyrics)[0].Romanization).Words!;
        Assert.Equal(1000, aligned[0].StartTimeMs); Assert.Equal(600, aligned[0].EndTimeMs - aligned[0].StartTimeMs);
        Assert.Equal(1600, aligned[1].StartTimeMs); Assert.Equal(800, aligned[1].EndTimeMs - aligned[1].StartTimeMs);
        Assert.Equal(1000 + offset, words[0].StartTimeMs);
        Assert.Same(words, lyrics.RomanizationLines![0].Words);
    }

    [Fact]
    public void MissingTranslationsAndMainTimelineGapsDoNotDisplayAnotherRowsAuxiliaryText()
    {
        var lyrics = new LemonLyrics("One\nTwo\nThree", Lines: [new("One", 1000, 2000), new("Two", 3000, 4000), new("Three", 5000, 6000)],
            TranslationLines: [new("一", 800), new("三", 5200)], RomanizationLines: [new([], "one", 800), new([], "three", 5200)]);
        Assert.Null(LyricsTimeline.CreateFrame(lyrics, 2500).Translation);
        Assert.Null(LyricsTimeline.CreateFrame(lyrics, 3000).Translation);
        Assert.Null(LyricsTimeline.CreateFrame(lyrics, 3000).Romanization);
        Assert.Equal("三", LyricsTimeline.CreateFrame(lyrics, 5000).Translation!.Text);
        Assert.Null(LyricsTimeline.CreateFrame(lyrics, 6000).Translation);
    }

    [Fact]
    public void ExactAndNearestTimestampMatchesWinOverIntervalMembership()
    {
        var lyrics = new LemonLyrics("One\nTwo\nThree", Lines: [new("One", 1000), new("Two", 1200), new("Three", 3000)],
            TranslationLines: [new("Nearest", 1110), new("Exact", 1200)]);
        var document = LyricsDocument.Create(lyrics);
        Assert.Empty(document[0].Translation);
        Assert.Equal(["Nearest", "Exact"], document[1].Translation.Select(line => line.Text));
        Assert.Equal("Exact", LyricsTimeline.CreateFrame(lyrics, 1200).Translation!.Text);
    }

    [Fact]
    public void AuxiliaryLinesInsideLongMainRowsRetainTheirOwnChanges()
    {
        var lyrics = new LemonLyrics("One\nTwo", Lines: [new("One", 0, 3000), new("Two", 4000, 5000)],
            TranslationLines: [new("First", 500), new("Second", 1500)]);
        Assert.Equal("First", LyricsTimeline.CreateFrame(lyrics, 600).Translation!.Text);
        Assert.Equal("Second", LyricsTimeline.CreateFrame(lyrics, 1600).Translation!.Text);
        Assert.Null(LyricsTimeline.CreateFrame(lyrics, 4000).Translation);
    }

    [Fact]
    public void EmptyAuxiliaryEndCuesDoNotClearTheMainRowsText()
    {
        var lyrics = new LemonLyrics("Repeated\nRepeated\nUntranslated", Lines:
            [new("Repeated", 1000, 2004), new("Repeated", 3000, 4007), new("Untranslated", 5000, 6000)],
            TranslationLines: [new("第一句", 800), new("", 2000), new("第二句", 3200), new(" \t", 4000), new("", 5000)],
            RomanizationLines: [new([], "first", 800), new([], "", 2000), new([], "second", 3200), new([], " \t", 4000)]);
        var document = LyricsDocument.Create(lyrics);
        foreach (var position in new[] { 1000, 2001, 2500, 5000, 1000 })
        {
            Assert.Equal("第一句", LyricDocumentRow.SelectAuxiliary(document[0].Translation, position)!.Text);
            Assert.Equal("first", LyricDocumentRow.SelectAuxiliary(document[0].Romanization, position)!.Text);
        }
        Assert.Equal("第一句", LyricsTimeline.CreateFrame(lyrics, 2001).Translation!.Text);
        Assert.Equal("first", LyricsTimeline.CreateFrame(lyrics, 2001).Romanization!.Text);
        Assert.Equal("第二句", LyricsTimeline.CreateFrame(lyrics, 4001).Translation!.Text);
        Assert.Equal("second", LyricsTimeline.CreateFrame(lyrics, 4001).Romanization!.Text);
        Assert.Null(LyricsTimeline.CreateFrame(lyrics, 2500).Translation);
        Assert.Null(LyricsTimeline.CreateFrame(lyrics, 5000).Translation);
        Assert.Null(LyricDocumentRow.SelectAuxiliary(document[2].Translation, 6000));
    }

    [Fact]
    public void QqTranslationPlaceholdersAreRemovedByThePluginAndKeepTheirTimestamps()
    {
        const string raw = "[00:01.000]Credits\n[00:02.000]A lyric\n[00:03.000]//";
        const string translation = "[00:01.000] // \r\n[00:02.000]Text containing // remains\r\n[00:03.000]//";
        var lyrics = LyricsConverter.Parse(raw, LyricsRawTypes.Lrc, MusicIdNamespaces.QQMusic,
            new(MusicIdNamespaces.QQMusic, "test", "song-id"), translation);
        Assert.Equal("", lyrics.TranslationLines![0].Text);
        Assert.Equal(1000, lyrics.TranslationLines[0].StartTimeMs);
        Assert.Equal("Text containing // remains", lyrics.TranslationLines[1].Text);
        Assert.Equal("", lyrics.TranslationLines[2].Text);
        Assert.DoesNotContain("[00:01.000] //", lyrics.Translation);
        Assert.Contains("Text containing // remains", lyrics.Translation);
        Assert.Equal("//", lyrics.Lines![2].Text); // The main lyric text is not a translation placeholder.
        Assert.Null(LyricsTimeline.CreateFrame(lyrics, 1000).Translation);
        Assert.Equal("Text containing // remains", LyricsTimeline.CreateFrame(lyrics, 2000).Translation!.Text);
        Assert.Null(LyricsTimeline.CreateFrame(lyrics, 3000).Translation);
    }

    [Fact]
    public void QqEmbeddedTranslationsAreCleanedAndOtherSourcesRemainUnchanged()
    {
        var data = new LyricsData { Lines = [new FullLineInfo { Text = "Credits", StartTime = 1000,
            Translations = new Dictionary<string, string> { ["zh"] = " // ", ["en"] = "Actual // text" } }] };
        var qq = LyricsConverter.Convert(data, "Credits", MusicIdNamespaces.QQMusic, new(MusicIdNamespaces.QQMusic, "test"));
        Assert.Equal("", qq.Lines![0].Translations!["zh"]);
        Assert.Equal("Actual // text", qq.Lines[0].Translations!["en"]);
        var other = LyricsConverter.Parse("[00:01.000]Credits", LyricsRawTypes.Lrc, MusicIdNamespaces.NeteaseMusic,
            new(MusicIdNamespaces.NeteaseMusic, "test"), "[00:01.000]//");
        Assert.Equal("//", other.TranslationLines![0].Text);
        Assert.Equal("//", LyricsTimeline.CreateFrame(other, 1000).Translation!.Text);
    }
}
