using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Plugin.LocalMusic;

public sealed class LocalMusicPlugin :
    ILemonPlugin,
    IMusicProvider,
    ISearchProvider,
    ITrackLookupProvider,
    IPlaybackProvider,
    IFolderImportProvider,
    ITrackIdResolverProvider
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aiff", ".flac", ".mp3", ".ogg", ".wav"
    };

    private readonly ConcurrentDictionary<string, Track> _catalog = new(StringComparer.Ordinal);

    public PluginDescriptor Descriptor { get; } = new(
        "org.lemonneo.localmusic",
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
        context.RegisterProvider(this);
        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _catalog.Clear();
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
        Track track,
        PlaybackRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(track.Id.ProviderId, Id, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<PlaybackSource?>(null);
        }

        var path = track.ExternalIds
            .FirstOrDefault(id => string.Equals(id.Namespace, "file", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (path is null && _catalog.TryGetValue(track.Id.RemoteId, out var catalogTrack))
        {
            path = catalogTrack.ExternalIds
                .FirstOrDefault(id => string.Equals(id.Namespace, "file", StringComparison.OrdinalIgnoreCase))
                ?.Value;
        }

        return Task.FromResult<PlaybackSource?>(path is not null && File.Exists(path)
            ? new FilePlaybackSource(path)
            : null);
    }

    public Task<Track?> ResolveTrackIdAsync(
        ExternalMusicId identifier,
        TrackMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AcceptedIdSchemes.Any(scheme => scheme.Accepts(identifier)) || !File.Exists(identifier.Value))
        {
            return Task.FromResult<Track?>(null);
        }

        return Task.FromResult<Track?>(CreateTrack(identifier.Value));
    }

    private static Track CreateTrack(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var fileName = Path.GetFileNameWithoutExtension(fullPath);
        var separator = fileName.LastIndexOf(" -", StringComparison.Ordinal);
        var title = separator > 0 ? fileName[..separator].Trim() : fileName.Trim();
        var artist = separator > 0 ? fileName[(separator + 2)..].Trim() : null;
        var normalizedPath = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath))).ToLowerInvariant();

        return new Track
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
    }
}
