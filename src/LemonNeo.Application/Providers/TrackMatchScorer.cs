using System.Text;
using System.Text.RegularExpressions;
using LemonNeo.Core.Music;

namespace LemonNeo.Application.Providers;

internal static partial class TrackMatchScorer
{
    public static (double Confidence, string Level) Compare(Track original, Track candidate)
    {
        if (original.Id == candidate.Id || SharesSongId(original, candidate)) return (1, "Same ID");
        if (HasConflictingSongId(original, candidate)) return (0, "No match");

        var title = Normalize(original.Title);
        if (title.Length == 0 || title != Normalize(candidate.Title)) return (0, "No match");

        var artists = NormalizeArtists(original);
        var candidateArtists = NormalizeArtists(candidate);
        if (!artists.Overlaps(candidateArtists)) return (0, "No match");
        if (!artists.SetEquals(candidateArtists)) return (0.7, "Partial artist match");

        var album = original.Album?.Title;
        var candidateAlbum = candidate.Album?.Title;
        if (!Editions(original).SetEquals(Editions(candidate))) return (0.65, "Different edition");

        var confidence = 0.88;
        if (original.Duration is { TotalSeconds: > 0 } duration &&
            candidate.Duration is { TotalSeconds: > 0 } candidateDuration)
        {
            var difference = Math.Abs((duration - candidateDuration).TotalSeconds);
            // A few seconds can be encoder padding or silence, but a substantially different
            // duration is evidence against reusing the same recording's ID.
            if (difference > Math.Max(8, Math.Min(duration.TotalSeconds, candidateDuration.TotalSeconds) * 0.04))
                return (0.65, "Different duration");
            confidence += difference switch { <= 2 => 0.06, <= 5 => 0.03, _ => -0.10 };
        }

        if (Normalize(album) is { Length: > 0 } albumTitle && albumTitle == Normalize(candidateAlbum))
            confidence += 0.02;
        return (confidence, confidence >= 0.90 ? "High" : confidence >= 0.85 ? "Likely" : "Possible");
    }

    private static bool SharesSongId(Track original, Track candidate) =>
        original.ExternalIds.Any(id => IsSongId(id) && candidate.ExternalIds.Any(other =>
            SameScheme(id, other) && string.Equals(id.Value, other.Value, StringComparison.Ordinal)));

    private static bool HasConflictingSongId(Track original, Track candidate) =>
        original.ExternalIds.Any(id => IsSongId(id) && candidate.ExternalIds.Any(other =>
            SameScheme(id, other) && !string.IsNullOrWhiteSpace(other.Value) &&
            !string.Equals(id.Value, other.Value, StringComparison.Ordinal)));

    private static bool IsSongId(ExternalMusicId id) => !string.IsNullOrWhiteSpace(id.Value) &&
        (string.Equals(id.Kind, "song-id", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(id.Kind, "song-mid", StringComparison.OrdinalIgnoreCase));

    private static bool SameScheme(ExternalMusicId id, ExternalMusicId other) =>
        string.Equals(id.Namespace, other.Namespace, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(id.Kind, other.Kind, StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> NormalizeArtists(Track track) =>
        track.Artists.Select(artist => Normalize(artist.Name)).Where(name => name.Length > 0).ToHashSet();

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = new StringBuilder(value.Length);
        foreach (var rune in value.Normalize(NormalizationForm.FormKC).EnumerateRunes())
            if (Rune.IsLetterOrDigit(rune)) text.Append(Rune.ToUpperInvariant(rune).ToString());
        return text.ToString();
    }

    private static HashSet<string> Editions(Track track) => EditionPattern().Matches($"{track.Title} {track.Album?.Title}")
        .Select(match => match.Value.ToUpperInvariant() switch
        {
            "现场" or "現場" or "演唱会" or "演唱會" => "LIVE",
            "混音" => "REMIX",
            "伴奏" => "INSTRUMENTAL",
            "REMASTERED" => "REMASTER",
            var edition => edition
        }).ToHashSet();

    [GeneratedRegex(@"\b(?:live|remix|acoustic|instrumental|karaoke|demo|remaster(?:ed)?|radio edit)\b|现场|現場|演唱会|演唱會|混音|伴奏",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EditionPattern();
}
