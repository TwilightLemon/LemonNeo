using System.Text.Json;
using LemonNeo.Application.Lyrics;
using LemonNeo.Core.Lyrics;
using LemonNeo.Core.Music;
using Microsoft.Extensions.Logging;
using LyricsModel = LemonNeo.Core.Lyrics.Lyrics;

namespace LemonNeo.Application;

/// <summary>
///     Host-owned lyrics storage. The host supplies its resolved cache directory.
///     Documents are shared by platform, ID kind and value, independently of the fetching plugin.
/// </summary>
public sealed class LyricsCache(Func<string> getCacheDirectory, ILogger<LyricsCache> logger)
{
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IgnoreReadOnlyProperties = true,
        RespectNullableAnnotations = true
    };

    public string DirectoryPath => Path.Combine(Path.GetFullPath(getCacheDirectory()), "Lyrics");

    public async Task<LyricsLookupResult?> TryGetAsync(ExternalMusicId identifier,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        identifier = MusicCacheIdentity.Normalize(identifier);
        var directory = DirectoryPath;
        var path = GetPath(directory, identifier);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var entry = await JsonSerializer.DeserializeAsync<CacheEntry>(stream, JsonOptions, cancellationToken);
            if (entry is null || entry.FormatVersion != FormatVersion ||
                entry.Identifier != identifier || !IsValid(entry.Lyrics)) return null;

            return new LyricsLookupResult(entry.Lyrics, entry.ProviderId, new Dictionary<string, string>());
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "Could not read cached lyrics for {MusicId}; fetching lyrics from providers.",
                identifier);
            return null;
        }
    }

    public async Task StoreAsync(ExternalMusicId identifier, LyricsModel lyrics, string? providerId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValid(lyrics)) return;
        identifier = MusicCacheIdentity.Normalize(identifier);
        var directory = DirectoryPath;
        var temporaryPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream,
                    new CacheEntry(FormatVersion, identifier, lyrics, providerId), JsonOptions, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, GetPath(directory, identifier), true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "Could not cache lyrics for {MusicId}. Fetched lyrics remain available.",
                identifier);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not remove the temporary lyrics cache file {Path}.", temporaryPath);
            }
        }
    }

    private static string GetPath(string directory, ExternalMusicId identifier)
    {
        return Path.Combine(directory, MusicCacheIdentity.GetKey(identifier) + ".json");
    }

    internal static bool IsValid(LyricsModel? lyrics)
    {
        return lyrics is not null && lyrics.Text is not null &&
               (!string.IsNullOrWhiteSpace(lyrics.Text) ||
                lyrics.Lines?.Any(line => !string.IsNullOrWhiteSpace(line?.Text)) == true) &&
               lyrics.Lines?.All(IsValidLine) != false && lyrics.TranslationLines?.All(IsValidLine) != false &&
               lyrics.RomanizationLines?.All(line =>
                   line is not null && line.Words is not null && line.Words.All(IsValidWord)) != false;
    }

    private static bool IsValidLine(LyricsLine? line)
    {
        return line is not null && line.Text is not null &&
               line.Words?.All(IsValidWord) != false &&
               (line.BackgroundLine is null || IsValidLine(line.BackgroundLine));
    }

    private static bool IsValidWord(LyricsWord? word)
    {
        return word is not null && word.Text is not null;
    }

    private sealed record CacheEntry(
        int FormatVersion,
        ExternalMusicId Identifier,
        LyricsModel Lyrics,
        // Provenance only; never part of the key or required for a cache hit.
        string? ProviderId = null);
}
