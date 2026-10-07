using Avalonia.Headless.XUnit;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Music;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Metadata;
using Xunit;

namespace LemonNeo.Hosting.Tests;

public sealed class TrackEditorMetadataTests
{
    [AvaloniaFact]
    public void MatchedTrackDisplaysNamesAndAppliesMetadataWithoutOverwritingEditedIdentifiers()
    {
        using var text = new LocalizationService();
        var original = HostFixture.Track() with
        {
            ArtworkUri = new("https://example.com/saved.png"),
            ExternalIds = [new("isrc", "ABC000000001")]
        };
        var matched = new Track
        {
            Id = new("metadata-broker", "matched"),
            Title = "Matched title",
            Artists = [new ArtistRef { Id = new("netease", "artist-1"), Name = "Matched artist" }],
            Album = new AlbumRef { Id = new("netease", "album-1"), Title = "Matched album" },
            Duration = TimeSpan.FromSeconds(180),
            ExternalIds = [new("netease", "12345", "song-id")]
        };
        var row = new MetadataMatchRow(new()
        {
            MetadataProviderId = "metadata-broker",
            SourceNamespace = "netease",
            Track = matched,
            Confidence = .96,
            MatchLevel = "test"
        }, text, new(_ => { }), new(_ => { }));
        Assert.Equal("Matched artist", row.Artists);
        Assert.Equal("Matched album", row.Album);
        Assert.Equal(matched.Title, row.Title);
        Assert.False(row.HasActionStatus);
        row.MarkMetadataReplaced();
        Assert.True(row.HasActionStatus);
        Assert.Equal(text["MetadataReplacedConfirmation"], row.ActionStatus);
        row.MarkExternalIdsAdded();
        Assert.Equal(text["MetadataIdsAddedConfirmation"], row.ActionStatus);

        var editor = new TrackEditorViewModel(original, text)
        {
            ProviderId = "library",
            TrackId = "edited-id"
        };
        editor.ExternalIds[0].Value = "edited-isrc";
        editor.ApplyMetadata(matched);
        var saved = editor.Build();

        Assert.Equal(new("library", "edited-id"), saved.Id);
        Assert.Equal(matched.Title, saved.Title);
        Assert.Equal(matched.Artists, saved.Artists);
        Assert.Same(matched.Album, saved.Album);
        Assert.Equal(matched.Duration, saved.Duration);
        Assert.Equal(original.ArtworkUri, saved.ArtworkUri);
        Assert.Equal(new("isrc", "edited-isrc"), Assert.Single(saved.ExternalIds));
    }
}
