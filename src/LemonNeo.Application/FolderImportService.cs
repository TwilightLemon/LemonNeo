using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Application;

public sealed record FolderImportResult(
    Playlist Playlist,
    int ImportedTracks,
    IReadOnlyList<string> SkippedFiles);

public sealed class FolderImportService(
    IProviderRegistry registry,
    PlaylistService playlists)
{
    public async Task<FolderImportResult> ImportAsync(
        string directoryPath,
        string? playlistName = null,
        bool recursive = true,
        string providerId = "local",
        CancellationToken cancellationToken = default)
    {
        var scan = await ScanAsync(directoryPath, recursive, providerId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ImportScan(scan, playlistName);
    }

    /// <summary>Reads provider data without changing the shared library.</summary>
    public async Task<FolderScanResult> ScanAsync(
        string directoryPath,
        bool recursive = true,
        string providerId = "local",
        CancellationToken cancellationToken = default)
    {
        var provider = registry.Get(providerId)
            ?? throw new ProviderException($"Provider '{providerId}' was not found.");
        if (provider is not IFolderImportProvider folderProvider)
        {
            throw new ProviderException($"Provider '{providerId}' does not support folder import.");
        }

        FolderScanResult scan;
        try
        {
            scan = await folderProvider.ScanFolderAsync(
                new FolderScanRequest(directoryPath, recursive),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ProviderException)
        {
            throw new ProviderException($"Could not scan music folder '{directoryPath}'.", exception);
        }

        return scan;
    }

    public FolderImportResult ImportScan(FolderScanResult scan, string? playlistName = null)
    {
        var playlist = playlists.Create(
            string.IsNullOrWhiteSpace(playlistName) ? scan.SuggestedPlaylistName : playlistName,
            $"Imported from {scan.DirectoryPath}");
        foreach (var track in scan.Tracks)
        {
            playlists.Add(playlist.Id, track);
        }

        return new FolderImportResult(playlist, scan.Tracks.Count, scan.SkippedFiles);
    }
}
