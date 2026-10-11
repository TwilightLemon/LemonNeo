using System.Security.Cryptography;
using System.Text;
using LemonNeo.Abstractions.Playback;
using LemonNeo.Core.Library;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using Microsoft.Extensions.Logging;
using TagLib;
using File = TagLib.File;

namespace LemonNeo.Application.Library;

public sealed record FolderScanResult(
    string DirectoryPath,
    string SuggestedPlaylistName,
    IReadOnlyList<Track> Tracks,
    IReadOnlyList<string> SkippedFiles);

public sealed record FolderImportResult(
    Playlist Playlist,
    int ImportedTracks,
    IReadOnlyList<string> SkippedFiles);

public sealed class FolderImportService(
    IPlayerRegistry players,
    PlaylistService playlists,
    Func<string> getCacheDirectory,
    ILogger<FolderImportService> logger)
{
    public async Task<FolderImportResult> ImportAsync(
        string directoryPath,
        string? playlistName = null,
        bool recursive = true,
        CancellationToken cancellationToken = default)
    {
        var scan = await ScanAsync(directoryPath, recursive, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ImportScan(scan, playlistName);
    }

    /// <summary>Reads local files without changing the shared library.</summary>
    public async Task<FolderScanResult> ScanAsync(
        string directoryPath,
        bool recursive = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var directory = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Music folder was not found: {directory}");

        var decoders = players.Players
            .SelectMany(player => player.SupportedExtensions.Select(extension =>
                (Extension: NormalizeExtension(extension), Player: player)))
            .Where(item => item.Extension is not null)
            .GroupBy(item => item.Extension!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Player).Distinct().ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var tracks = new List<Track>();
        var skipped = new List<string>();
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };

        await Task.Yield();
        foreach (var path in Directory.EnumerateFiles(directory, "*", enumerationOptions)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!decoders.TryGetValue(Path.GetExtension(path), out var candidates) ||
                !candidates.Any(player => player.CanPlay(new FilePlaybackSource(path))))
                continue;

            try
            {
                tracks.Add(CreateTrack(path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or ArgumentException)
            {
                logger.LogDebug(exception, "Could not import local audio file {Path}.", path);
                skipped.Add(path);
            }
        }

        var suggestedName = new DirectoryInfo(directory).Name;
        if (string.IsNullOrWhiteSpace(suggestedName)) suggestedName = "Local Music";
        return new FolderScanResult(directory, suggestedName, tracks, skipped);
    }

    public FolderImportResult ImportScan(FolderScanResult scan, string? playlistName = null)
    {
        var playlist = playlists.Create(
            string.IsNullOrWhiteSpace(playlistName) ? scan.SuggestedPlaylistName : playlistName,
            $"Imported from {scan.DirectoryPath}");
        foreach (var track in scan.Tracks) playlists.Add(playlist.Id, track);
        return new FolderImportResult(playlist, scan.Tracks.Count, scan.SkippedFiles);
    }

    private Track CreateTrack(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var fileName = Path.GetFileNameWithoutExtension(fullPath);
        var separator = fileName.LastIndexOf(" -", StringComparison.Ordinal);
        var title = separator > 0 ? fileName[..separator].Trim() : fileName.Trim();
        var artist = separator > 0 ? fileName[(separator + 2)..].Trim() : null;
        var normalizedPath = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
        var fallback = new Track
        {
            Id = new MusicItemId("local", id),
            Title = title,
            Artists = string.IsNullOrWhiteSpace(artist) ? [] : [new ArtistRef { Name = artist }],
            ExternalIds = [new ExternalMusicId(MusicIdNamespaces.File, fullPath, "path")]
        };

        try
        {
            using var file = File.Create(fullPath, ReadStyle.Average);
            var tag = file.Tag;
            var artists = CleanNames(tag.Performers);
            if (artists.Length == 0) artists = CleanNames(tag.AlbumArtists);
            return fallback with
            {
                Title = CleanText(tag.Title) ?? fallback.Title,
                Artists = artists.Length > 0
                    ? artists.Select(name => new ArtistRef { Name = name }).ToArray()
                    : fallback.Artists,
                Album = CleanText(tag.Album) is { } album ? new AlbumRef { Title = album } : null,
                Duration = file.Properties.Duration > TimeSpan.Zero ? file.Properties.Duration : null,
                ArtworkUri = ReadArtwork(tag.Pictures)
            };
        }
        catch (Exception exception) when (exception is CorruptFileException or UnsupportedFormatException or
                                              IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogDebug(exception, "Could not read tags from {Path}; using its filename.", fullPath);
            return fallback;
        }
    }

    private Uri? ReadArtwork(IEnumerable<IPicture> pictures)
    {
        var artworkDirectory = Path.Combine(getCacheDirectory(), "Artwork");
        foreach (var picture in pictures.OrderByDescending(picture => picture.Type == PictureType.FrontCover))
        {
            if (picture.Data.Count is <= 0 or > 16 * 1024 * 1024) continue;
            var bytes = picture.Data.Data;
            var extension = ImageExtension(bytes);
            if (extension is null) continue;
            string? temporary = null;
            try
            {
                Directory.CreateDirectory(artworkDirectory);
                var path = Path.Combine(artworkDirectory, Convert.ToHexStringLower(SHA256.HashData(bytes)) + extension);
                if (!System.IO.File.Exists(path))
                {
                    temporary = Path.Combine(artworkDirectory, Guid.NewGuid().ToString("N") + ".part");
                    System.IO.File.WriteAllBytes(temporary, bytes);
                    System.IO.File.Move(temporary, path, true);
                }

                return new Uri(Path.GetFullPath(path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(exception, "Could not extract embedded artwork.");
                return null;
            }
            finally
            {
                if (temporary is not null)
                    try
                    {
                        System.IO.File.Delete(temporary);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                    }
            }
        }

        return null;
    }

    private static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        var value = extension.Trim();
        return value[0] == '.' ? value : "." + value;
    }

    private static string? CleanText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string[] CleanNames(IEnumerable<string> names)
    {
        return names.Select(CleanText).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? ImageExtension(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
        if (data.StartsWith(new byte[] { 255, 216, 255 })) return ".jpg";
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)) return ".gif";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
            return ".webp";
        return null;
    }
}