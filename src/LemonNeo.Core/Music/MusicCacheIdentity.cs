using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LemonNeo.Core.Music;

/// <summary>Stable cache identities shared by catalogue snapshots from different providers.</summary>
public static class MusicCacheIdentity
{
    // Native IDs have no external scheme. Keep their own explicit kind instead of aliasing an external ID.
    public static ExternalMusicId FromTrackId(MusicItemId id)
    {
        return new ExternalMusicId(id.ProviderId, id.RemoteId, "track-id");
    }

    public static IEnumerable<ExternalMusicId> KnownIds(Track track)
    {
        return new[] { FromTrackId(track.Id) }.Concat(track.ExternalIds).DistinctBy(GetKey);
    }

    public static ExternalMusicId Normalize(ExternalMusicId identifier)
    {
        return identifier with
        {
            Namespace = identifier.Namespace.ToUpperInvariant(),
            Kind = identifier.Kind?.ToUpperInvariant()
        };
    }

    public static ExternalMusicId[] GetIdentifiers(IEnumerable<ExternalMusicId> identifiers)
    {
        return identifiers
            .Where(identifier => identifier is not null &&
                                 !string.IsNullOrWhiteSpace(identifier.Namespace) &&
                                 !string.IsNullOrWhiteSpace(identifier.Value))
            .Select(Normalize)
            .Distinct()
            .OrderBy(identifier => identifier.Namespace, StringComparer.Ordinal)
            .ThenBy(identifier => identifier.Kind, StringComparer.Ordinal)
            .ThenBy(identifier => identifier.Value, StringComparer.Ordinal)
            .ToArray();
    }

    public static string GetKey(ExternalMusicId identifier)
    {
        var normalized = Normalize(identifier);
        var identity = JsonSerializer.Serialize(new
        {
            normalized.Namespace,
            Type = normalized.Kind,
            Id = normalized.Value
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}