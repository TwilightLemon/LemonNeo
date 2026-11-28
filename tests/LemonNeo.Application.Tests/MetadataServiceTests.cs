using LemonNeo.Abstractions.Providers;
using LemonNeo.Application.Providers;
using LemonNeo.Core.Music;
using Xunit;

namespace LemonNeo.Application.Tests;

public sealed class MetadataServiceTests
{
    [Theory]
    [InlineData(null, "Matched album", "album-42", 1)]
    [InlineData(null, "Another edition", null, 1)]
    [InlineData("existing-album", "Another edition", "existing-album", 0)]
    public async Task MatchAndReplaceKeepsOrResolvesAlbumId(string? existingAlbumId, string lookupAlbumTitle,
        string? expectedAlbumId, int expectedLookups)
    {
        var provider = new Provider(lookupAlbumTitle);
        var registry = new Registry(provider);
        var service = new MetadataService(registry, new TrackLookupService(registry));
        var original = new Track
        {
            Id = new MusicItemId("local", "song"),
            Title = "Song",
            Artists = [new ArtistRef { Name = "Artist" }],
            Album = new AlbumRef
            {
                Title = "Matched album",
                Id = existingAlbumId is null ? null : new MusicItemId("netease", existingAlbumId)
            }
        };

        var (updated, _) = await service.MatchAndReplaceAsync(original, ["netease"]);

        Assert.Equal(original.Id, updated.Id);
        Assert.Equal("Matched album", updated.Album?.Title);
        Assert.Equal(expectedAlbumId, updated.Album?.Id?.RemoteId);
        Assert.Equal(expectedLookups, provider.LookupCount);
    }

    [Fact]
    public async Task BracketedTitleAndMatchingArtistOutrankTheOriginalPerformersVersion()
    {
        var match = Song("netease", "537637239", "カトラリー（翻自 初音ミク）", "Sawako碎花", 200);
        var other = Song("netease", "1899686964", "カトラリー", "神山羊 / 初音ミク", 200);
        var provider = new Provider("Record") { Candidates = [other, match] };
        var registry = new Registry(provider);
        var service = new MetadataService(registry, new TrackLookupService(registry));
        var original = Song("local", "file", "カトラリー", "Sawako碎花", 200);

        var result = await service.LookupAsync(original, ["netease"], minimumConfidence: 0);

        Assert.Equal(match.Id, result.Matches[0].Track.Id);
        Assert.InRange(result.Matches[0].Confidence, 0.9, 0.99);
        Assert.InRange(result.Matches[1].Confidence, 0.01, 0.79);
    }

    [Fact]
    public async Task BatchMatchScoresAllCandidatesBeforeSelectingOne()
    {
        var studio = Song("netease", "studio", "Song", "Artist", 180) with
        {
            Album = new AlbumRef { Title = "Studio record", Id = new MusicItemId("netease", "album-42") }
        };
        var provider = new Provider("Studio record")
        {
            Candidates =
            [
                Song("netease", "live", "Song", "Artist", 181) with { Album = new AlbumRef { Title = "Live in Tokyo" } },
                Song("netease", "cover", "Song", "Different Artist", 180),
                studio
            ]
        };
        var registry = new Registry(provider);
        var service = new MetadataService(registry, new TrackLookupService(registry));
        var original = Song("local", "file", "Ｓｏｎｇ", "artist", 180) with
        {
            Album = new AlbumRef { Title = "Studio record" },
            ExternalIds = []
        };

        var (updated, result) = await service.MatchAndReplaceAsync(original, ["netease"]);

        Assert.Equal(studio.Id, Assert.Single(result.Matches).Track.Id);
        Assert.Equal(studio.Album, updated.Album);
        Assert.Equal("studio", Assert.Single(updated.ExternalIds).Value);
        Assert.Equal(0, provider.LookupCount);
        Assert.True(provider.SearchLimit > 1);
    }

    [Theory]
    [InlineData("Song (Live)", "Artist", 180, null)]
    [InlineData("Song", "Cover Artist", 180, null)]
    [InlineData("Song", "Artist", 240, null)]
    [InlineData("Song", "Artist", 180, "Live in Tokyo")]
    public async Task PlaybackExpansionRejectsDifferentRecordings(string title, string artist, int duration, string? album)
    {
        var candidate = Song("netease", "remote", title, artist, duration) with
        {
            Album = album is null ? null : new AlbumRef { Title = album }
        };
        var provider = new Provider("Record") { Candidates = [candidate] };
        var registry = new Registry(provider);
        var service = new MetadataService(registry, new TrackLookupService(registry));
        var original = Song("local", "file", "Song", "Artist", 180);

        var result = await service.LookupAsync(original, ["netease"], 2, 0.85);

        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task KnownPlatformIdTakesPriorityOverDescriptiveMetadata()
    {
        var candidate = Song("netease", "known", "Localized title", "Localized artist", 180);
        var provider = new Provider("Record") { Candidates = [candidate] };
        var registry = new Registry(provider);
        var service = new MetadataService(registry, new TrackLookupService(registry));
        var original = Song("local", "file", "Song", "Artist", 180) with { ExternalIds = candidate.ExternalIds };

        var result = await service.LookupAsync(original, ["netease"]);

        Assert.Equal(1d, Assert.Single(result.Matches).Confidence);
    }

    private static Track Song(string provider, string id, string title, string artist, int seconds) => new()
    {
        Id = new MusicItemId(provider, id),
        Title = title,
        Artists = [new ArtistRef { Name = artist }],
        Duration = TimeSpan.FromSeconds(seconds),
        ExternalIds = [new ExternalMusicId(provider, id, "song-id")]
    };

    private sealed class Registry(Provider provider) : IProviderRegistry
    {
        public IReadOnlyCollection<IMusicProvider> Providers { get; } = [provider];
        public IMusicProvider? Get(string providerId) => provider.Id == providerId ? provider : null;
    }

    private sealed class Provider(string lookupAlbumTitle)
        : IMusicProvider, ISearchProvider, ITrackLookupProvider, ICatalogProvider
    {
        public string Id => "netease";
        public ProviderDescriptor Descriptor { get; } = new("Test source", "");
        public IReadOnlyCollection<string> SearchNamespaces { get; } = ["netease"];
        public int LookupCount { get; private set; }
        public int SearchLimit { get; private set; }
        public IReadOnlyList<Track>? Candidates { get; init; }

        public Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Id, request.SourceNamespace);
            SearchLimit = request.Limit;
            IReadOnlyList<Track> candidates = Candidates ??
                [Song(Id, "song-123", "Song", "Artist", 180) with { Album = new AlbumRef { Title = "Matched album" } }];
            return Task.FromResult(new SearchResult(Id, candidates.Take(request.Limit).ToArray()));
        }

        public Task<Track?> GetTrackAsync(string trackId, CancellationToken cancellationToken = default)
        {
            Assert.Equal("song-123", trackId);
            LookupCount++;
            return Task.FromResult<Track?>(new Track
            {
                Id = new MusicItemId(Id, trackId),
                Title = "Song",
                Album = new AlbumRef { Title = lookupAlbumTitle, Id = new MusicItemId(Id, "album-42") }
            });
        }

        public Task<IReadOnlyList<Album>> SearchAlbumsAsync(SearchRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Artist>> SearchArtistsAsync(SearchRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AlbumDetail?> GetAlbumAsync(string albumId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Artist?> GetArtistAsync(string artistId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
