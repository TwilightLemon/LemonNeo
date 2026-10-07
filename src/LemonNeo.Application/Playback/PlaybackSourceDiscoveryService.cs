using LemonNeo.Abstractions.Providers;
using LemonNeo.Application.Providers;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;

namespace LemonNeo.Application.Playback;

public sealed record PlaybackSourceCandidate(
    string ProviderId,
    Track Track,
    PlaybackSource Source,
    ExternalMusicId SourceId,
    string ResolutionMethod,
    bool IsCached = false);

public sealed record PlaybackSourceDiscoveryResult(
    IReadOnlyList<PlaybackSourceCandidate> Candidates,
    IReadOnlyDictionary<string, string> ProviderErrors,
    Track? UpdatedTrack = null,
    bool MetadataExpanded = false);

public sealed class PlaybackSourceDiscoveryService(
    IPlaybackSourceProviderRegistry playbackSources,
    MetadataService metadataService,
    PlaybackAudioCache audioCache)
{
    private readonly PlaybackAudioCache _audioCache = audioCache;
    private const double MinimumMetadataConfidence = 0.85;
    private static readonly MusicIdScheme LocalFileScheme = new(MusicIdNamespaces.File, "path");

    // The detail page inspects every known identity independently, without metadata lookup.
    public async Task<PlaybackSourceDiscoveryResult> FindKnownAsync(
        Track track, PlaybackRequest? request = null, CancellationToken cancellationToken = default)
    {
        request ??= new PlaybackRequest();
        var search = new Search(this, request, cancellationToken);
        var candidates = new List<PlaybackSourceCandidate>();
        foreach (var identity in GetIdentities(track))
        {
            PlaybackSourceCandidate? candidate = null;
            foreach (var quality in AudioQualities.SearchOrder(request.Quality))
            {
                candidate = await search.ResolveAsync(identity, quality);
                if (candidate is not null) break;
            }
            candidate ??= search.UnknownFor(identity);
            if (candidate is not null) candidates.Add(candidate);
        }
        return new(candidates, search.Errors, track);
    }

    // An explicit local file is the preferred recording; quality ranks the alternatives only.
    public async Task<PlaybackSourceDiscoveryResult> FindAsync(
        Track track, PlaybackRequest? request = null, CancellationToken cancellationToken = default, bool expandMetadata = true)
    {
        request ??= new PlaybackRequest();
        var search = new Search(this, request, cancellationToken);
        var known = GetIdentities(track).Where(identity => request.RequiredSourceId is null ||
            MusicCacheIdentity.GetKey(identity.Id) == MusicCacheIdentity.GetKey(request.RequiredSourceId)).ToArray();
        var qualities = AudioQualities.SearchOrder(request.Quality).ToArray();
        foreach (var identity in known.Where(identity => LocalFileScheme.Accepts(identity.Id)))
            if (await search.ResolveLocalFileAsync(identity, qualities[0]) is { } local)
                return new([local], search.Errors, track);

        // Multiple external namespaces mean this track has already been expanded, regardless of ID kind.
        var hasExpandedSources = track.ExternalIds.Select(id => id.Namespace)
            .Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();
        List<SourceIdentity>? expanded = expandMetadata && request.RequiredSourceId is null && !hasExpandedSources ? null : [];
        var metadataExpanded = false;
        var updatedTrack = track;
        foreach (var quality in qualities)
        {
            foreach (var identity in known)
                if (await search.ResolveAsync(identity, quality) is { } candidate)
                    return new([candidate], search.Errors, updatedTrack, metadataExpanded);

            if (expanded is null)
            {
                expanded = [];
                var namespaces = SelectedProviders(request).SelectMany(p => p.AcceptedIdSchemes)
                    .Select(s => s.Namespace).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (namespaces.Length > 0)
                {
                    metadataExpanded = true;
                    var lookup = await metadataService.LookupAsync(track, namespaces, limitPerSource: 2,
                        minimumConfidence: MinimumMetadataConfidence, cancellationToken: cancellationToken);
                    foreach (var error in lookup.SourceErrors) search.Errors[$"metadata/{error.Key}"] = error.Value;
                    var seen = known.Select(i => MusicCacheIdentity.GetKey(i.Id)).ToHashSet();
                    foreach (var match in lookup.Matches.Where(m => m.Confidence >= MinimumMetadataConfidence)
                                 .OrderByDescending(m => m.Confidence))
                    {
                        foreach (var identity in GetIdentities(match.Track))
                        {
                            if (!ProvidersFor(identity, request).Any() || !seen.Add(MusicCacheIdentity.GetKey(identity.Id))) continue;
                            expanded.Add(identity with
                            {
                                Method = $"metadata-match:{match.MetadataProviderId}/{match.SourceNamespace}"
                            });
                        }
                    }
                    // Metadata confidence belongs to the identity match, never to the audio URL.
                    updatedTrack = track with
                    {
                        ExternalIds = track.ExternalIds.Concat(expanded.Select(i => i.Id))
                            .DistinctBy(MusicCacheIdentity.GetKey).ToArray()
                    };
                }
            }
            foreach (var identity in expanded)
                if (await search.ResolveAsync(identity, quality) is { } candidate)
                    return new([candidate], search.Errors, updatedTrack, metadataExpanded);
        }
        var unknown = known.Concat(expanded ?? []).Select(search.UnknownFor).FirstOrDefault(c => c is not null);
        return new(unknown is null ? [] : [unknown], search.Errors, updatedTrack, metadataExpanded);
    }

    private sealed record SourceIdentity(ExternalMusicId Id, Track Track, string? NativeProviderId, string Method);

    private static IEnumerable<SourceIdentity> GetIdentities(Track track)
    {
        var native = MusicCacheIdentity.FromTrackId(track.Id);
        yield return new(native, track, track.Id.ProviderId, "provider-native-id");
        foreach (var id in track.ExternalIds.DistinctBy(MusicCacheIdentity.GetKey))
        {
            if (MusicCacheIdentity.GetKey(id) == MusicCacheIdentity.GetKey(native)) continue;
            yield return new(id, track, string.Equals(id.Kind, "track-id", StringComparison.OrdinalIgnoreCase) ? id.Namespace : null, $"declared-id:{id.Namespace}/{id.Kind}");
        }
    }

    private IEnumerable<PlaybackSourceProviderRegistration> SelectedProviders(PlaybackRequest request) =>
        playbackSources.Sources.Where(p => request.RequiredSourceProviderId is null ||
            string.Equals(p.ProviderId, request.RequiredSourceProviderId, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<PlaybackSourceProviderRegistration> ProvidersFor(SourceIdentity identity, PlaybackRequest request) =>
        SelectedProviders(request).Where(p => identity.NativeProviderId is { } native
            ? string.Equals(p.ProviderId, native, StringComparison.OrdinalIgnoreCase)
            : p.AcceptedIdSchemes.Any(s => s.Accepts(identity.Id)));

    // All state is local to a single search, including URLs silently downgraded by third parties.
    private sealed class Search(PlaybackSourceDiscoveryService owner, PlaybackRequest request, CancellationToken cancellationToken)
    {
        private readonly Dictionary<(string Identity, string Provider, AudioQuality Quality), PlaybackSource?> _attempts = [];
        private readonly List<PlaybackSourceCandidate> _deferred = [];
        public PlaybackSourceCandidate? UnknownFor(SourceIdentity identity) => _deferred.FirstOrDefault(c =>
            MusicCacheIdentity.GetKey(c.SourceId) == MusicCacheIdentity.GetKey(identity.Id) && c.Source.Quality is null);

        public Dictionary<string, string> Errors { get; } = new(StringComparer.OrdinalIgnoreCase);

        public async Task<PlaybackSourceCandidate?> ResolveLocalFileAsync(SourceIdentity identity, AudioQuality quality)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(identity.Id.Value)) return null;
            // Use the normal provider restrictions, but do not compare an original file's
            // quality (which may be unknown) with cache entries or remote replacements.
            foreach (var provider in owner.ProvidersFor(identity, request))
                if (await ResolveProviderAsync(identity, provider, quality) is { Source: FilePlaybackSource file } candidate &&
                    File.Exists(file.Path)) return candidate;
            return null;
        }

        public async Task<PlaybackSourceCandidate?> ResolveAsync(SourceIdentity identity, AudioQuality quality)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = MusicCacheIdentity.GetKey(identity.Id);
            var providers = owner.ProvidersFor(identity, request).ToArray();
            // A required provider restricts eligible identities, not the cache key.
            var allowed = request.RequiredSourceProviderId is null || request.RequiredSourceId is not null || providers.Length > 0 ||
                string.Equals(identity.NativeProviderId, request.RequiredSourceProviderId, StringComparison.OrdinalIgnoreCase);
            if (!allowed) return null;
            if (await owner._audioCache.TryGetAsync(identity.Id, quality, cancellationToken) is { } cached)
                return new(providers.FirstOrDefault()?.ProviderId ?? identity.NativeProviderId ?? identity.Id.Namespace,
                    identity.Track, cached, identity.Id, "cache", IsCached: true);

            var retained = _deferred.Where(c => MusicCacheIdentity.GetKey(c.SourceId) == key && c.Source.Quality >= quality)
                .OrderByDescending(c => c.Source.Quality).FirstOrDefault();
            if (retained is not null) return retained;
            foreach (var provider in providers)
            {
                if (_deferred.Any(c => MusicCacheIdentity.GetKey(c.SourceId) == key &&
                    c.ProviderId == provider.ProviderId && c.Source.Quality is null)) continue;
                var candidate = await ResolveProviderAsync(identity, provider, quality);
                if (candidate is null) continue;
                if (candidate.Source.Quality >= quality) return candidate;
                _deferred.Add(candidate);
            }
            return null;
        }

        private async Task<PlaybackSourceCandidate?> ResolveProviderAsync(
            SourceIdentity identity, PlaybackSourceProviderRegistration provider, AudioQuality quality)
        {
            var attempt = (MusicCacheIdentity.GetKey(identity.Id), provider.ProviderId, quality);
            if (!_attempts.TryGetValue(attempt, out var source))
            {
                try
                {
                    var qualityRequest = request.Quality == quality ? request : request with { Quality = quality };
                    source = await provider.PlaybackProvider.ResolvePlaybackAsync(
                        new(provider.ProviderId, identity.Id.Value), qualityRequest, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Errors[$"{provider.ProviderId}/{identity.Id.Value}/{quality}"] = exception.Message;
                }
                _attempts[attempt] = source;
            }
            if (source is null) return null;
            if (source.Quality == AudioQuality.Automatic) source = source with { Quality = null };
            var resolvedTrack = identity.Track.Id == new MusicItemId(provider.ProviderId, identity.Id.Value) ? identity.Track :
                identity.Track with { Id = new(provider.ProviderId, identity.Id.Value) };
            return new(provider.ProviderId, resolvedTrack, source, identity.Id, identity.Method);
        }
    }
}
