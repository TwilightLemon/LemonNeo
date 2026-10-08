using LemonNeo.Abstractions.Plugins;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LemonNeo.Plugin.LocalMusic;

public sealed class LocalMusicPlugin :
    ILemonPlugin,
    IMusicProvider,
    ISearchProvider,
    ITrackLookupProvider,
    IPlaybackProvider,
    IFolderImportProvider
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aiff", ".flac", ".mp3", ".ogg", ".wav"
    };

    private readonly ConcurrentDictionary<string, Track> _catalog = new(StringComparer.Ordinal);
    private IPluginContext? _context;
    private ILogger _logger = NullLogger.Instance;

    public PluginDescriptor Descriptor { get; } = new(
        "LemonNeo.Plugin.LocalMusic",
        "Local Music",
        "1.0.0");

    public string Id => "local";

    ProviderDescriptor IMusicProvider.Descriptor { get; } = new(
        "Local Music",
        "Scans local folders and resolves local audio files for playback.");

    public IReadOnlyCollection<MusicIdScheme> AcceptedIdSchemes { get; } =
    [
        new(MusicIdNamespaces.File, "path")
    ];

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context = context;
        _logger = context.LoggerFactory.CreateLogger<LocalMusicPlugin>();
        context.RegisterProvider(this);
        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _catalog.Clear();
        _context = null;
        return ValueTask.CompletedTask;
    }

    public Task<SearchResult> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tracks = _catalog.Values
            .Where(track => string.IsNullOrWhiteSpace(request.Keyword) ||
                            track.Title.Contains(request.Keyword, StringComparison.OrdinalIgnoreCase) ||
                            track.Artists.Any(artist =>
                                artist.Name.Contains(request.Keyword, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(track => track.Title, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, request.Limit))
            .ToArray();
        return Task.FromResult(new SearchResult(Id, tracks));
    }

    public Task<Track?> GetTrackAsync(
        string trackId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_catalog.GetValueOrDefault(trackId));
    }

    public async Task<FolderScanResult> ScanFolderAsync(
        FolderScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DirectoryPath);
        var directory = Path.GetFullPath(request.DirectoryPath);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Music folder was not found: {directory}");
        }

        var tracks = new List<Track>();
        var skipped = new List<string>();
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = request.Recursive,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };

        await Task.Yield();
        foreach (var path in Directory.EnumerateFiles(directory, "*", enumerationOptions)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SupportedExtensions.Contains(Path.GetExtension(path)))
            {
                continue;
            }

            try
            {
                var track = CreateTrack(path);
                _catalog[track.Id.RemoteId] = track;
                tracks.Add(track);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                skipped.Add(path);
            }
        }

        var suggestedName = new DirectoryInfo(directory).Name;
        if (string.IsNullOrWhiteSpace(suggestedName))
        {
            suggestedName = "Local Music";
        }

        return new FolderScanResult(directory, suggestedName, tracks, skipped);
    }

    public Task<PlaybackSource?> ResolvePlaybackAsync(
        MusicItemId trackId,
        PlaybackRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(trackId.ProviderId, Id, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<PlaybackSource?>(null);
        }

        var path = _catalog.TryGetValue(trackId.RemoteId, out var catalogTrack)
            ? catalogTrack.ExternalIds.FirstOrDefault(id => AcceptedIdSchemes.Any(scheme => scheme.Accepts(id)))?.Value
            : trackId.RemoteId;

        return Task.FromResult<PlaybackSource?>(path is not null && File.Exists(path)
            ? new FilePlaybackSource(path)
            : null);
    }

    private Track CreateTrack(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var fileName = Path.GetFileNameWithoutExtension(fullPath);
        var separator = fileName.LastIndexOf(" -", StringComparison.Ordinal);
        var title = separator > 0 ? fileName[..separator].Trim() : fileName.Trim();
        var artist = separator > 0 ? fileName[(separator + 2)..].Trim() : null;
        var normalizedPath = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath))).ToLowerInvariant();

        var fallback = new Track
        {
            Id = new MusicItemId("local", id),
            Title = title,
            Artists = string.IsNullOrWhiteSpace(artist) ? [] : [new ArtistRef { Name = artist }],
            ExternalIds =
            [
                new ExternalMusicId(
                    MusicIdNamespaces.File,
                    fullPath,
                    "path")
            ]
        };
        try
        {
            using var file = TagLib.File.Create(fullPath, TagLib.ReadStyle.Average);
            var tag = file.Tag;
            var artists = CleanNames(tag.Performers);
            if (artists.Length == 0) artists = CleanNames(tag.AlbumArtists);
            return fallback with
            {
                Title = CleanText(tag.Title) ?? fallback.Title,
                Artists = artists.Length > 0 ? artists.Select(name => new ArtistRef { Name = name }).ToArray() : fallback.Artists,
                Album = CleanText(tag.Album) is { } album ? new AlbumRef { Title = album } : null,
                Duration = file.Properties.Duration > TimeSpan.Zero ? file.Properties.Duration : null,
                ArtworkUri = ReadArtwork(tag.Pictures)
            };
        }
        catch (Exception exception) when (exception is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or
            IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A missing or damaged tag must not prevent importing a playable local file.
            _logger.LogDebug(exception, "Could not read tags from {Path}; using its filename.", fullPath);
            return fallback;
        }
    }

    private static string? CleanText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string[] CleanNames(IEnumerable<string> names) => names.Select(CleanText).OfType<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private Uri? ReadArtwork(IEnumerable<TagLib.IPicture> pictures)
    {
        if (_context is null) return null;
        var artworkDirectory = Path.Combine(_context.CacheDirectory, "Artwork");
        foreach (var picture in pictures.OrderByDescending(picture => picture.Type == TagLib.PictureType.FrontCover))
        {
            // Match the host image size limit. Do not persist URLs or arbitrary attachments as artwork.
            if (picture.Data.Count is <= 0 or > 16 * 1024 * 1024) continue;
            var bytes = picture.Data.Data;
            var extension = ImageExtension(bytes);
            if (extension is null) continue;
            string? temporary = null;
            try
            {
                Directory.CreateDirectory(artworkDirectory);
                var path = Path.Combine(artworkDirectory, Convert.ToHexStringLower(SHA256.HashData(bytes)) + extension);
                if (!File.Exists(path))
                {
                    temporary = Path.Combine(artworkDirectory, Guid.NewGuid().ToString("N") + ".part");
                    File.WriteAllBytes(temporary, bytes);
                    File.Move(temporary, path, overwrite: true);
                }
                return new Uri(Path.GetFullPath(path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Artwork storage failure must not discard the title, artists or album tags.
                _logger.LogDebug(exception, "Could not extract embedded artwork.");
                return null;
            }
            finally
            {
                if (temporary is not null)
                    try { File.Delete(temporary); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
        return null;
    }

    private static string? ImageExtension(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
        if (data.StartsWith(new byte[] { 255, 216, 255 })) return ".jpg";
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)) return ".gif";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)) return ".webp";
        return null;
    }
}
