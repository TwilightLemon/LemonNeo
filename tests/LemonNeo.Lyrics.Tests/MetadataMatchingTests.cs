using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;
using LemonNeo.Plugin.Lyricify;
using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;
using Xunit;
using HelperSearchers = Lyricify.Lyrics.Searchers.Searchers;

namespace LemonNeo.Lyrics.Tests;

public sealed class MetadataMatchingTests
{
    private static Track CurrentTrack => new()
    {
        Id = new("original-provider", "original-id"), Title = "Original song",
        Artists = [new() { Name = "Original artist" }], Duration = TimeSpan.FromSeconds(180)
    };

    [Fact]
    public async Task CustomKeywordsSearchIndependentlyAndRankAgainstTheOriginalTrackBeforeLimiting()
    {
        var unrelated = new QQMusicSearchResult("Alternative keywords", ["Different artist"], "", null, 400000, "wrong", "wrong-mid")
            { MatchType = CompareHelper.MatchType.Perfect };
        var exact = new QQMusicSearchResult("Original song", ["Original artist"], "", null, 180000, "exact", "exact-mid")
            { MatchType = CompareHelper.MatchType.NoMatch };
        var searcher = new FixtureSearcher([unrelated, exact]);
        var request = new MetadataLookupRequest
        {
            Track = CurrentTrack, SearchKeyword = "  Alternative keywords  ", LimitPerSource = 1, MinimumConfidence = 0
        };
        var matches = await LyricifyMetadataPlugin.SearchMetadataMatchesAsync(searcher, MusicIdNamespaces.QQMusic, request,
            TestContext.Current.CancellationToken);
        Assert.Equal("Alternative keywords", Assert.Single(searcher.Queries));
        var match = Assert.Single(matches);
        Assert.Equal("Original song", match.Metadata.Title);
        Assert.Equal(1, match.Confidence);
        Assert.Equal("exact", Assert.Single(match.Metadata.ExternalIds, id => id.Kind == "song-id").Value);
        Assert.Equal("Original song", request.Track.Title);
        Assert.Equal(CompareHelper.MatchType.NoMatch, exact.MatchType);
    }

    [Fact]
    public async Task AutomaticMatchingStillUsesTheTrackAndFiltersLowConfidenceCandidates()
    {
        var searcher = new FixtureSearcher([
            new QQMusicSearchResult("Different song", ["Different artist"], "", null, 400000, "wrong", "wrong-mid"),
            new QQMusicSearchResult("Original song", ["Original artist"], "", null, 180000, "exact", "exact-mid")]);
        var matches = await LyricifyMetadataPlugin.SearchMetadataMatchesAsync(searcher, MusicIdNamespaces.QQMusic,
            new() { Track = CurrentTrack, MinimumConfidence = 1 }, TestContext.Current.CancellationToken);
        Assert.Equal("Original song Original artist", Assert.Single(searcher.Queries));
        Assert.Equal("Original song", Assert.Single(matches).Metadata.Title);
    }

    [Fact]
    public async Task EmptyCustomSearchResponsesReturnNoMatches()
    {
        var matches = await LyricifyMetadataPlugin.SearchMetadataMatchesAsync(new FixtureSearcher(null), MusicIdNamespaces.QQMusic,
            new() { Track = CurrentTrack, SearchKeyword = "nothing" }, TestContext.Current.CancellationToken);
        Assert.Empty(matches);
    }

    [Fact]
    public async Task CancelledMatchingDoesNotStartASourceSearch()
    {
        var searcher = new FixtureSearcher([]);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LyricifyMetadataPlugin.SearchMetadataMatchesAsync(searcher,
            MusicIdNamespaces.QQMusic, new() { Track = CurrentTrack, SearchKeyword = "cancelled" }, cancellation.Token));
        Assert.Empty(searcher.Queries);
    }

    private sealed class FixtureSearcher(List<ISearchResult>? results) : Searcher
    {
        public List<string> Queries { get; } = [];
        public override string Name => "fixture";
        public override string DisplayName => "Fixture";
        public override HelperSearchers SearcherType => HelperSearchers.QQMusic;
        public override Task<List<ISearchResult>?> SearchForResults(string searchString)
        {
            Queries.Add(searchString);
            return Task.FromResult(results);
        }
    }
}
