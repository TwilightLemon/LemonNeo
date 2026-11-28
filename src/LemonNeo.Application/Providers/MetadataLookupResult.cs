using LemonNeo.Core.Music;

namespace LemonNeo.Application.Providers;

public sealed record MetadataMatch(string MetadataProviderId, string SourceNamespace, Track Track,
    double Confidence, string MatchLevel);

public sealed record MetadataLookupResult(IReadOnlyList<MetadataMatch> Matches,
    IReadOnlyDictionary<string, string> SourceErrors);
