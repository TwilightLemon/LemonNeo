using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.ConsoleApp;

internal enum ProviderParameterRole
{
    TrackLookup,
    Authentication,
    Playlist,
    PlaybackSource
}

internal static class ProviderParameterRules
{
    public static string Placeholder(ProviderParameterRole role) => role switch
    {
        ProviderParameterRole.TrackLookup => "<track-provider>",
        ProviderParameterRole.Authentication => "<auth-provider>",
        ProviderParameterRole.Playlist => "<playlist-provider>",
        ProviderParameterRole.PlaybackSource => "<source-provider>",
        _ => "<provider>"
    };

    public static string Title(ProviderParameterRole role) => role switch
    {
        ProviderParameterRole.TrackLookup => "Track providers",
        ProviderParameterRole.Authentication => "Authentication providers",
        ProviderParameterRole.Playlist => "Playlist providers",
        ProviderParameterRole.PlaybackSource => "Playback source providers",
        _ => "Providers"
    };

    public static string Requirement(ProviderParameterRole role) => role switch
    {
        ProviderParameterRole.TrackLookup => "must support track lookup by native ID",
        ProviderParameterRole.Authentication => "must support authentication",
        ProviderParameterRole.Playlist => "must support playlist access",
        ProviderParameterRole.PlaybackSource => "must resolve playable sources",
        _ => "must support the required capability"
    };

    public static bool Supports(IMusicProvider provider, ProviderParameterRole role) => role switch
    {
        ProviderParameterRole.TrackLookup => provider is ITrackLookupProvider,
        ProviderParameterRole.Authentication => provider is IAuthenticationProvider,
        ProviderParameterRole.Playlist => provider is IPlaylistProvider,
        ProviderParameterRole.PlaybackSource => provider is IPlaybackProvider,
        _ => false
    };

    public static IReadOnlyList<IMusicProvider> Allowed(
        IProviderRegistry registry,
        ProviderParameterRole role,
        IPlaybackSourceProviderRegistry? playbackSources = null) =>
        (role == ProviderParameterRole.PlaybackSource && playbackSources is not null
            ? playbackSources.Sources.Select(source => source.MusicProvider)
            : registry.Providers.Where(provider => Supports(provider, role)))
            .OrderBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string AllowedValues(
        IProviderRegistry registry,
        ProviderParameterRole role,
        IPlaybackSourceProviderRegistry? playbackSources = null)
    {
        var ids = Allowed(registry, role, playbackSources).Select(provider => provider.Id).ToArray();
        return ids.Length == 0 ? "(none)" : string.Join(", ", ids);
    }

    public static IMusicProvider GetRequired(
        IProviderRegistry registry,
        string providerId,
        ProviderParameterRole role,
        IPlaybackSourceProviderRegistry? playbackSources = null)
    {
        var provider = Allowed(registry, role, playbackSources)
            .FirstOrDefault(candidate => string.Equals(
                candidate.Id,
                providerId,
                StringComparison.OrdinalIgnoreCase));
        if (provider is not null)
        {
            return provider;
        }

        var parameter = Placeholder(role);
        var allowed = AllowedValues(registry, role, playbackSources);
        if (registry.Get(providerId) is null)
        {
            throw new ProviderException(
                $"Provider '{providerId}' was not found for {parameter}. Allowed values: {allowed}.");
        }

        throw new ProviderException(
            $"Provider '{providerId}' cannot be used as {parameter}: it {Requirement(role)}. " +
            $"Allowed values: {allowed}.");
    }
}
