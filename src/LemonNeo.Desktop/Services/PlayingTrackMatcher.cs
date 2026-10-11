using LemonNeo.Core.Music;

namespace LemonNeo.Desktop.Services;

public static class PlayingTrackMatcher
{
    // Native IDs use the same explicit kind as playback discovery and the audio cache.
    // Scheme names are case insensitive; provider ID values remain case sensitive.
    public static bool Matches(Track track, ExternalMusicId? sourceId)
    {
        return sourceId is not null &&
               (SameId(MusicCacheIdentity.FromTrackId(track.Id), sourceId) ||
                track.ExternalIds.Any(id => SameId(id, sourceId)));
    }

    private static bool SameId(ExternalMusicId left, ExternalMusicId right)
    {
        return string.Equals(left.Namespace, right.Namespace, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.Value, right.Value, StringComparison.Ordinal);
    }
}