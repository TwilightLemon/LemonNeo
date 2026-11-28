using System.Text;
using System.Text.RegularExpressions;
using LemonNeo.Core.Music;

namespace LemonNeo.Application.Providers;

internal static partial class TrackMatchScorer
{
    public static (double Confidence, string Level) Compare(Track original, Track candidate)
    {
        if (original.Id == candidate.Id || SharesSongId(original, candidate)) return (1, "Same ID");

        // Like Lyricify's CompareHelper, compare each field independently and redistribute
        // the weight of optional fields when one side has no value.
        var title = CompareTitle(original.Title, candidate.Title);
        var artists = NormalizeArtists(original);
        var candidateArtists = NormalizeArtists(candidate);
        var artist = artists.Count > 0 && candidateArtists.Count > 0
            ? (double)artists.Count(candidateArtists.Contains) / Math.Max(artists.Count, candidateArtists.Count)
            : (double?)null;
        var album = CompareAlbum(original.Album?.Title, candidate.Album?.Title);
        var duration = CompareDuration(original.Duration, candidate.Duration);
        var confidence = title * 0.48;
        var weight = 0.48;
        Add(artist, 0.42);
        Add(duration, 0.25);
        Add(album, 0.08);
        confidence /= weight;

        if (title < 0.82) confidence = Math.Min(confidence, 0.69);
        if (artist is null or < 1) confidence = Math.Min(confidence, 0.79);
        if (HasConflictingSongId(original, candidate)) return (Math.Min(confidence, 0.74), "Different song ID");

        var editions = Editions(original);
        if (!editions.SetEquals(Editions(candidate)) ||
            editions.Count > 0 && title < 0.95)
            return (Math.Min(confidence, 0.74), "Different edition");
        if (duration is 0) return (Math.Min(confidence, 0.74), "Different duration");

        return (Math.Min(confidence, 0.99), confidence switch
        {
            >= 0.90 => "High",
            >= 0.85 => "Likely",
            > 0 => "Possible",
            _ => "No match"
        });

        void Add(double? value, double fieldWeight)
        {
            if (value is not { } score) return;
            confidence += score * fieldWeight;
            weight += fieldWeight;
        }
    }

    private static double CompareTitle(string left, string right)
    {
        var fullLeft = Normalize(left);
        var fullRight = Normalize(right);
        if (fullLeft.Length == 0 || fullRight.Length == 0) return 0;
        if (fullLeft == fullRight) return 1;

        var baseLeft = Normalize(TitleSuffix().Replace(left.Normalize(NormalizationForm.FormKC), ""));
        var baseRight = Normalize(TitleSuffix().Replace(right.Normalize(NormalizationForm.FormKC), ""));
        if (baseLeft.Length > 0 && baseLeft == baseRight) return 0.88;
        return Similarity(fullLeft, fullRight);
    }

    private static double? CompareAlbum(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) ? CompareTitle(left, right) : null;

    private static double? CompareDuration(TimeSpan? left, TimeSpan? right)
    {
        if (left is not { TotalSeconds: > 0 } actual || right is not { TotalSeconds: > 0 } candidate) return null;
        var difference = Math.Abs((actual - candidate).TotalSeconds);
        return difference switch
        {
            <= 0.7 => 1,
            <= 2 => 0.96,
            <= 5 => 0.85,
            <= 8 => 0.5,
            _ => 0
        };
    }

    private static double Similarity(string left, string right)
    {
        var length = Math.Max(left.Length, right.Length);
        if (length is < 4 or > 256 || Math.Abs(left.Length - right.Length) > length / 2) return 0;
        var row = new int[right.Length + 1];
        for (var j = 0; j < row.Length; j++) row[j] = j;
        for (var i = 0; i < left.Length; i++)
        {
            var diagonal = row[0]++;
            for (var j = 0; j < right.Length; j++)
            {
                var above = row[j + 1];
                row[j + 1] = Math.Min(Math.Min(above, row[j]) + 1, diagonal + (left[i] == right[j] ? 0 : 1));
                diagonal = above;
            }
        }
        return 1d - (double)row[^1] / length;
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
        track.Artists.SelectMany(artist => ArtistSeparator().Split(artist.Name))
            .Select(Normalize).Where(name => name.Length > 0).ToHashSet();

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
            "翻唱版" => "COVER",
            "REMASTERED" => "REMASTER",
            var edition => edition
        }).ToHashSet();

    [GeneratedRegex(@"\b(?:live|remix|acoustic|instrumental|karaoke|demo|cover|remaster(?:ed)?|radio edit)\b|现场|現場|演唱会|演唱會|混音|伴奏|翻唱版",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EditionPattern();

    [GeneratedRegex(@"(?:\s*(?:\([^()]+\)|\[[^\[\]]+\]|【[^【】]+】)|\s+[-–—]\s+.+)+\s*$")]
    private static partial Regex TitleSuffix();

    [GeneratedRegex(@"\s+(?:/|&|＆|／)\s+|[;；、]")]
    private static partial Regex ArtistSeparator();
}
