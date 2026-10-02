using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Application;

/// <summary>
/// Host-owned lyrics storage. The host supplies its resolved cache directory;
/// providers do not choose paths or need to be available for a cache hit.
/// </summary>
public sealed class LyricsCache(string cacheDirectory, ILogger<LyricsCache> logger)
{
    private const int FormatVersion = 1;
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
        var path = GetPath(track);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var entry = await JsonSerializer.DeserializeAsync<CacheEntry>(stream, JsonOptions, cancellationToken);
            if (entry is null || entry.FormatVersion != FormatVersion ||
                !string.Equals(entry.TrackId.ProviderId, track.Id.ProviderId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(entry.TrackId.RemoteId, track.Id.RemoteId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(entry.ProviderId) || !IsValid(entry.Lyrics)) return null;

            return new LyricsLookupResult(entry.Lyrics, entry.ProviderId, new Dictionary<string, string>());
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "Could not read cached lyrics for {TrackId}; fetching lyrics from providers.", track.Id);
            return null;
        }
    }

    public async Task StoreAsync(Track track, Lyrics lyrics, string providerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValid(lyrics)) return;
        var temporaryPath = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            Directory.CreateDirectory(_directory);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream,
                    new CacheEntry(FormatVersion, track.Id, providerId, lyrics), JsonOptions, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, GetPath(track), overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "Could not cache lyrics for {TrackId}. Fetched lyrics remain available.", track.Id);
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

    private string GetPath(Track track)
    {
        var identity = JsonSerializer.Serialize(new
        {
            ProviderId = track.Id.ProviderId.ToUpperInvariant(),
            track.Id.RemoteId
        });
        return Path.Combine(_directory,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json");
    }

    private static bool IsValid(Lyrics? lyrics) => lyrics is not null && lyrics.Text is not null &&
        (!string.IsNullOrWhiteSpace(lyrics.Text) || lyrics.Lines?.Any(line => !string.IsNullOrWhiteSpace(line?.Text)) == true) &&
        lyrics.Lines?.All(IsValidLine) != false && lyrics.TranslationLines?.All(IsValidLine) != false &&
        lyrics.RomanizationLines?.All(line => line is not null && line.Words is not null && line.Words.All(IsValidWord)) != false;

    private static bool IsValidLine(LyricsLine? line) => line is not null && line.Text is not null &&
        line.Words?.All(IsValidWord) != false && (line.BackgroundLine is null || IsValidLine(line.BackgroundLine));

    private static bool IsValidWord(LyricsWord? word) => word is not null && word.Text is not null;

    private sealed record CacheEntry(int FormatVersion, MusicItemId TrackId, string ProviderId, Lyrics Lyrics);
}
