using LemonNeo.Application.Lyrics;
using LemonNeo.Core.Lyrics;
using LyricsModel = LemonNeo.Core.Lyrics.Lyrics;
using LemonNeo.Core.Music;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Application;

/// <summary>
/// Host-owned lyrics storage. The host supplies its resolved cache directory;
/// providers do not choose paths or need to be available for a cache hit.
/// </summary>
public sealed class LyricsCache(string cacheDirectory, ILogger<LyricsCache> logger)
{
    private const int FormatVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IgnoreReadOnlyProperties = true,
        RespectNullableAnnotations = true
    };
    private readonly string _directory = Path.Combine(Path.GetFullPath(cacheDirectory), "Lyrics");

    public async Task<LyricsLookupResult?> TryGetAsync(Track track, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identifiers = MusicCacheIdentity.GetIdentifiers(track.ExternalIds);
        foreach (var identifier in identifiers)
        {
            var result = await TryGetAsync(identifier, cancellationToken);
            if (result is null) continue;
            foreach (var alias in identifiers)
            {
                if (alias != identifier && !File.Exists(GetPath(alias)))
                    await StoreAsync(alias, result.Lyrics!, result.ProviderId!, cancellationToken);
            }
            return result;
        }
        return null;
    }

    private async Task<LyricsLookupResult?> TryGetAsync(ExternalMusicId identifier, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(identifier);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var entry = await JsonSerializer.DeserializeAsync<CacheEntry>(stream, JsonOptions, cancellationToken);
            if (entry is null || entry.FormatVersion != FormatVersion ||
                entry.Identifier != identifier ||
                string.IsNullOrWhiteSpace(entry.ProviderId) || !IsValid(entry.Lyrics)) return null;

            return new LyricsLookupResult(entry.Lyrics, entry.ProviderId, new Dictionary<string, string>());
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "Could not read cached lyrics for {MusicId}; fetching lyrics from providers.", identifier);
            return null;
        }
    }

    public async Task StoreAsync(Track track, LyricsModel lyrics, string providerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValid(lyrics)) return;
        var identifiers = MusicCacheIdentity.GetIdentifiers(lyrics.SourceId is { } sourceId
            ? track.ExternalIds.Append(sourceId)
            : track.ExternalIds);
        foreach (var identifier in identifiers)
            await StoreAsync(identifier, lyrics, providerId, cancellationToken);
    }

    private async Task StoreAsync(ExternalMusicId identifier, LyricsModel lyrics, string providerId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var temporaryPath = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            Directory.CreateDirectory(_directory);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream,
                    new CacheEntry(FormatVersion, identifier, providerId, lyrics), JsonOptions, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, GetPath(identifier), overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "Could not cache lyrics for {MusicId}. Fetched lyrics remain available.", identifier);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not remove the temporary lyrics cache file {Path}.", temporaryPath);
            }
        }
    }

    private string GetPath(ExternalMusicId identifier) =>
        Path.Combine(_directory, MusicCacheIdentity.GetKey(identifier) + ".json");

    private static bool IsValid(LyricsModel? lyrics) => lyrics is not null && lyrics.Text is not null &&
        (!string.IsNullOrWhiteSpace(lyrics.Text) || lyrics.Lines?.Any(line => !string.IsNullOrWhiteSpace(line?.Text)) == true) &&
        lyrics.Lines?.All(IsValidLine) != false && lyrics.TranslationLines?.All(IsValidLine) != false &&
        lyrics.RomanizationLines?.All(line => line is not null && line.Words is not null && line.Words.All(IsValidWord)) != false;

    private static bool IsValidLine(LyricsLine? line) => line is not null && line.Text is not null &&
        line.Words?.All(IsValidWord) != false && (line.BackgroundLine is null || IsValidLine(line.BackgroundLine));

    private static bool IsValidWord(LyricsWord? word) => word is not null && word.Text is not null;

    private sealed record CacheEntry(int FormatVersion, ExternalMusicId Identifier, string ProviderId, LyricsModel Lyrics);
}
