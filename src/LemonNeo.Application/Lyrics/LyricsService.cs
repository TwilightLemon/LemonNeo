using LemonNeo.Abstractions.Providers;
using LemonNeo.Application;
using LemonNeo.Core.Lyrics;
using LyricsModel = LemonNeo.Core.Lyrics.Lyrics;
using LemonNeo.Core.Music;

namespace LemonNeo.Application.Lyrics;

public sealed record LyricsLookupResult(
    LyricsModel? Lyrics,
    string? ProviderId,
    IReadOnlyDictionary<string, string> ProviderErrors);

public sealed class LyricsService(IProviderRegistry registry, LyricsCache? cache = null)
{
    public async Task<LyricsLookupResult> GetLyricsAsync(
        Track track,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cache is not null && await cache.TryGetAsync(track, cancellationToken) is { } cached) return cached;

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var providers = registry.Providers
            .Where(provider => provider is ILyricsProvider)
            .OrderByDescending(provider => provider.Id == track.Id.ProviderId)
            .ThenByDescending(provider => provider is IMetadataProvider);

        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var lyrics = await ((ILyricsProvider)provider).GetLyricsAsync(track, cancellationToken);
                if (lyrics is not null)
                {
                    if (cache is not null) await cache.StoreAsync(track, lyrics, provider.Id, cancellationToken);
                    return new LyricsLookupResult(lyrics, provider.Id, errors);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors[provider.Id] = exception.Message;
            }
        }

        return new LyricsLookupResult(null, null, errors);
    }
}
