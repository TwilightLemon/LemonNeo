using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LemonNeo.Desktop.Services;

/// <summary>Checks the repository's latest stable release without downloading or replacing app files.</summary>
public sealed class GitHubReleaseClient(HttpClient httpClient) : IDisposable
{
    public const string ReleasesAddress = "https://github.com/TwilightLemon/LemonNeo/releases";
    private const string LatestReleaseAddress = "https://api.github.com/repos/TwilightLemon/LemonNeo/releases/latest";

    // Release builds are stamped with -p:Version by the packaging workflow. AssemblyVersion
    // loses prerelease information; InformationalVersion also includes an optional commit hash.
    public static string CurrentVersion { get; } =
        (typeof(GitHubReleaseClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public async Task<DesktopRelease?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseAddress);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LemonNeo.Desktop", CurrentVersion));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        // GitHub returns 404 when there are no published stable releases yet.
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<ReleaseResponse>(stream,
            cancellationToken: cancellationToken);
        if (release is null) throw new InvalidDataException("GitHub returned an empty release.");
        if (release.Draft || release.Prerelease) return null;
        if (!TryParseVersion(release.TagName, out _, out var prerelease) || prerelease)
            throw new InvalidDataException("The latest release tag is not a stable version.");

        // Build the URL from the trusted repository rather than opening a URL supplied by the API.
        return new DesktopRelease(release.TagName,
            new Uri($"{ReleasesAddress}/tag/{Uri.EscapeDataString(release.TagName)}"));
    }

    public static bool IsNewerVersion(string releaseVersion, string currentVersion)
    {
        if (!TryParseVersion(releaseVersion, out var latest, out var latestPrerelease) || latestPrerelease ||
            !TryParseVersion(currentVersion, out var current, out var currentPrerelease))
            throw new InvalidDataException("Cannot compare the application and release versions.");

        return latest > current || latest == current && currentPrerelease;
    }

    private static bool TryParseVersion(string? value, out Version version, out bool prerelease)
    {
        version = new Version();
        prerelease = false;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim();
        if (normalized[0] is 'v' or 'V') normalized = normalized[1..];
        normalized = normalized.Split('+')[0];
        var parts = normalized.Split('-', 2);
        prerelease = parts.Length == 2;
        if (Version.TryParse(parts[0], out var parsed))
        {
            // System.Version treats missing components as -1; tags and assembly versions
            // with equivalent numeric components must compare equal.
            version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
            return !prerelease || parts[1].Length > 0;
        }

        return false;
    }

    public void Dispose() => httpClient.Dispose();

    private sealed record ReleaseResponse(
        [property: JsonPropertyName("tag_name")] string TagName = "",
        [property: JsonPropertyName("draft")] bool Draft = false,
        [property: JsonPropertyName("prerelease")] bool Prerelease = false);
}

public sealed record DesktopRelease(string Version, Uri ReleaseUri);
