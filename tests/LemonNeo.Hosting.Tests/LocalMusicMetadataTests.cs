using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Plugins;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Abstractions.Storage;
using LemonNeo.Core.Music;
using LemonNeo.Plugin.LocalMusic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LemonNeo.Hosting.Tests;

public sealed class LocalMusicMetadataTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly byte[] Cover = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aY9sAAAAASUVORK5CYII=");

    [Theory]
    [InlineData(".wav")]
    [InlineData(".flac")]
    public async Task EmbeddedTagsAndFrontCoverTakePrecedenceWithoutModifyingTheMusicFile(string extension)
    {
        using var paths = new TemporaryPaths();
        var path = Path.Combine(paths.Root, "Filename title - Filename artist" + extension);
        if (extension == ".flac") AudioTestFiles.WriteFlacMetadata(path);
        else AudioTestFiles.WriteWave(path);
        var plugin = new LocalMusicPlugin();
        await plugin.InitializeAsync(new Context(paths.CacheDirectory), Token);
        var original = Assert.Single((await plugin.ScanFolderAsync(new(paths.Root), Token)).Tracks);
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Title = "  标签标题  ";
            file.Tag.Performers = ["歌手一", "歌手二"];
            file.Tag.Album = "专辑标题";
            file.Tag.Pictures =
            [
                new TagLib.Picture(new TagLib.ByteVector("GIF89a"u8.ToArray())) { Type = TagLib.PictureType.BackCover, MimeType = "image/gif" },
                new TagLib.Picture(new TagLib.ByteVector(Cover)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/png" }
            ];
            file.Save();
        }
        var before = await File.ReadAllBytesAsync(path, Token);

        var result = await plugin.ScanFolderAsync(new(paths.Root), Token);

        var track = Assert.Single(result.Tracks);
        Assert.Empty(result.SkippedFiles);
        Assert.Equal("标签标题", track.Title);
        Assert.Equal(new[] { "歌手一", "歌手二" }, track.Artists.Select(artist => artist.Name));
        Assert.Equal("专辑标题", track.Album?.Title);
        if (extension == ".wav") Assert.Equal(TimeSpan.FromSeconds(1), track.Duration);
        else Assert.Null(track.Duration); // The FLAC fixture contains metadata only, with no audio frames.
        Assert.Equal(original.Id, track.Id);
        Assert.Equal(original.ExternalIds, track.ExternalIds);
        Assert.True(track.ArtworkUri!.IsFile);
        Assert.Equal(Cover, await File.ReadAllBytesAsync(track.ArtworkUri.LocalPath, Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Token));
        await plugin.ShutdownAsync(Token);
        Assert.True(File.Exists(track.ArtworkUri.LocalPath));
        var fresh = new LocalMusicPlugin();
        await fresh.InitializeAsync(new Context(paths.CacheDirectory), Token);
        Assert.Equal(track.ArtworkUri, Assert.Single((await fresh.ScanFolderAsync(new(paths.Root), Token)).Tracks).ArtworkUri);
        Assert.Single(Directory.GetFiles(Path.Combine(paths.CacheDirectory, "Artwork")));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task MissingFieldsFallBackIndependently(bool hasTitle, bool hasArtist)
    {
        using var paths = new TemporaryPaths();
        var path = Path.Combine(paths.Root, "Filename title - Filename artist.wav");
        AudioTestFiles.WriteWave(path);
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Title = hasTitle ? "Tagged title" : "  ";
            file.Tag.Performers = hasArtist ? ["Tagged artist"] : [];
            file.Save();
        }
        var track = Assert.Single((await new LocalMusicPlugin().ScanFolderAsync(new(paths.Root), Token)).Tracks);
        Assert.Equal(hasTitle ? "Tagged title" : "Filename title", track.Title);
        Assert.Equal(hasArtist ? "Tagged artist" : "Filename artist", Assert.Single(track.Artists).Name);
        Assert.Null(track.ArtworkUri);
    }

    [Fact]
    public async Task AlbumArtistsAreUsedWhenPerformersAreMissing()
    {
        using var paths = new TemporaryPaths();
        var path = Path.Combine(paths.Root, "Only title.wav");
        AudioTestFiles.WriteWave(path);
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.AlbumArtists = ["Album artist"];
            file.Save();
        }
        var track = Assert.Single((await new LocalMusicPlugin().ScanFolderAsync(new(paths.Root), Token)).Tracks);
        Assert.Equal("Album artist", Assert.Single(track.Artists).Name);
    }

    [Fact]
    public async Task UnreadableTagsStillImportByFilenameAndPreservePlaybackIdentity()
    {
        using var paths = new TemporaryPaths();
        var path = Path.Combine(paths.Root, "Fallback title - Fallback artist.flac");
        await File.WriteAllBytesAsync(path, [1, 2, 3], Token);
        var result = await new LocalMusicPlugin().ScanFolderAsync(new(paths.Root), Token);
        var track = Assert.Single(result.Tracks);
        Assert.Empty(result.SkippedFiles);
        Assert.Equal("Fallback title", track.Title);
        Assert.Equal("Fallback artist", Assert.Single(track.Artists).Name);
        Assert.Equal(new ExternalMusicId("file", path, "path"), Assert.Single(track.ExternalIds));
    }

    [Fact]
    public async Task ArtworkStorageFailureKeepsOtherTags()
    {
        using var paths = new TemporaryPaths();
        var path = Path.Combine(paths.Root, "Fallback.wav");
        AudioTestFiles.WriteWave(path);
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Title = "Tagged title";
            file.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(Cover))];
            file.Save();
        }
        // A file occupies the cache directory path, preventing cover extraction.
        await File.WriteAllBytesAsync(paths.CacheDirectory, [0], Token);
        var plugin = new LocalMusicPlugin();
        await plugin.InitializeAsync(new Context(paths.CacheDirectory), Token);
        var track = Assert.Single((await plugin.ScanFolderAsync(new(paths.Root), Token)).Tracks);
        Assert.Equal("Tagged title", track.Title);
        Assert.Null(track.ArtworkUri);
    }

    private sealed class Context(string directory) : IPluginContext
    {
        public ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;
        public IHttpClientFactory HttpClientFactory => throw new NotSupportedException();
        public ISettingsScope Settings => throw new NotSupportedException();
        public ICredentialVault Credentials => throw new NotSupportedException();
        public string CacheDirectory => directory;
        public void RegisterProvider(IMusicProvider provider) { }
        public void RegisterPlayer(IPlayerProvider player) { }
    }
}
