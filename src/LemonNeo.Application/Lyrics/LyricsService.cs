using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Music;
using LyricsModel = LemonNeo.Core.Lyrics.Lyrics;

namespace LemonNeo.Application.Lyrics;

public sealed record LyricsLookupResult(
    LyricsModel? Lyrics,
    string? ProviderId,
    IReadOnlyDictionary<string, string> ProviderErrors);

/// <summary>Chooses one result from known platform IDs, sharing cached documents across plugins.</summary>
public sealed class LyricsService(IProviderRegistry registry, LyricsCache? cache = null)
{
    public async Task<LyricsLookupResult> GetLyricsAsync(Track track,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var known = MusicCacheIdentity.GetIdentifiers(track.ExternalIds.Prepend(MusicCacheIdentity.FromTrackId(track.Id)));
        var results = new List<Candidate>();
        var cachedIds = new HashSet<ExternalMusicId>();
        foreach (var id in known)
        {
            var cached = cache is null ? null : await cache.TryGetAsync(id, cancellationToken);
            if (cached is null) continue;
            results.Add(new Candidate(id, cached.ProviderId, Lyrics: cached.Lyrics, IsCached: true));
            cachedIds.Add(id);
        }

        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var best = Best(results);
        cancellationToken.ThrowIfCancellationRequested();
        // A cached result with every supported feature cannot be improved by another source.
        if (best is not null && Quality(best.Lyrics!) == (2, 2)) return Result(best, errors);

        var missing = registry.Providers.Where(provider => provider is ILyricsProvider)
            .SelectMany(provider => known.Where(id => !cachedIds.Contains(id) &&
                    ((ILyricsProvider)provider).AcceptedIdSchemes.Any(scheme => scheme.Accepts(id)))
                .Select(id => new Candidate(id, provider.Id, (ILyricsProvider)provider)))
            .OrderBy(candidate => candidate.ProviderId, StringComparer.Ordinal).ToArray();
        var fetched = await Task.WhenAll(missing.Select(candidate => FetchAsync(candidate, cancellationToken)));
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var (candidate, error) in fetched)
        {
            if (candidate.Lyrics is not null) results.Add(candidate);
            if (error is not null)
                errors[$"{candidate.ProviderId}/{candidate.Id.Namespace}:{candidate.Id.Kind}:{candidate.Id.Value}"] = error;
        }
        if (cache is not null)
            // Different plugins can fetch the same ID. Persist its best result once, not the last response.
            foreach (var group in fetched.Select(item => item.Candidate).Where(candidate => candidate.Lyrics is not null)
                         .GroupBy(candidate => candidate.Id))
            {
                var selected = Best(group)!;
                await cache.StoreAsync(selected.Id, selected.Lyrics!, selected.ProviderId, cancellationToken);
            }
        cancellationToken.ThrowIfCancellationRequested();
        return Result(Best(results), errors);
    }

    private async Task<(Candidate Candidate, string? Error)> FetchAsync(Candidate candidate, CancellationToken token)
    {
        try
        {
            var lyrics = await candidate.Provider!.GetLyricsAsync(candidate.Id, token).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (!LyricsCache.IsValid(lyrics)) return (candidate, null);
            return (candidate with { Lyrics = lyrics }, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { return (candidate, exception.Message); }
    }

    private static Candidate? Best(IEnumerable<Candidate> candidates) => candidates
        .OrderByDescending(candidate => Quality(candidate.Lyrics!))
        .ThenByDescending(candidate => candidate.IsCached)
        .ThenBy(candidate => candidate.Id.Namespace, StringComparer.Ordinal)
        .ThenBy(candidate => candidate.Id.Kind, StringComparer.Ordinal)
        .ThenBy(candidate => candidate.Id.Value, StringComparer.Ordinal)
        .ThenBy(candidate => candidate.ProviderId, StringComparer.Ordinal).FirstOrDefault();

    private static LyricsLookupResult Result(Candidate? candidate, IReadOnlyDictionary<string, string> errors) =>
        new(candidate?.Lyrics, candidate?.ProviderId, errors);

    private static (int Synchronization, int AuxiliaryKinds) Quality(LyricsModel lyrics)
    {
        var words = lyrics.Lines?.Any(line => !string.IsNullOrWhiteSpace(line.Text) &&
            line.Words?.Any(word => !string.IsNullOrWhiteSpace(word.Text) &&
                word.StartTimeMs >= 0 && word.EndTimeMs > word.StartTimeMs) == true) == true;
        var lines = lyrics.Lines?.Any(line => !string.IsNullOrWhiteSpace(line.Text) && line.StartTimeMs >= 0) == true;
        var translation = LyricsTimeline.PlainAuxiliaryLines(lyrics.Translation).Length > 0 ||
            lyrics.TranslationLines?.Any(line => !string.IsNullOrWhiteSpace(line.Text)) == true ||
            lyrics.Lines?.Any(line => line.Translations?.Values.Any(text => !string.IsNullOrWhiteSpace(text)) == true) == true;
        var romanization = LyricsTimeline.PlainAuxiliaryLines(lyrics.Romanization).Length > 0 ||
            lyrics.RomanizationLines?.Any(line => !string.IsNullOrWhiteSpace(line.Text)) == true ||
            lyrics.Lines?.Any(line => !string.IsNullOrWhiteSpace(line.Romanization)) == true;
        return (words ? 2 : lines ? 1 : 0, (translation ? 1 : 0) + (romanization ? 1 : 0));
    }

    private sealed record Candidate(ExternalMusicId Id, string? ProviderId, ILyricsProvider? Provider = null,
        LyricsModel? Lyrics = null, bool IsCached = false);
}
