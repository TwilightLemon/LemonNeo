using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Exceptions;
using LemonNeo.Core.Music;

namespace LemonNeo.Application.Providers;

public sealed class TrackLookupService(IProviderRegistry registry)
{
    public async Task<Track?> GetTrackAsync(
        string providerId,
        string trackId,
        CancellationToken cancellationToken = default)
    {
        var provider = registry.Get(providerId)
            ?? throw new ProviderException($"Provider '{providerId}' was not found.");
        if (provider is not ITrackLookupProvider lookupProvider)
        {
            throw new ProviderException($"Provider '{providerId}' does not support track lookup by ID.");
        }

        Track? track;
        try
        {
            track = await lookupProvider.GetTrackAsync(trackId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ProviderException)
        {
            throw new ProviderException(
                $"Could not retrieve track '{trackId}' from provider '{providerId}'.",
                exception);
        }

        if (track is not null &&
            (!string.Equals(track.Id.ProviderId, provider.Id, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(track.Id.RemoteId, trackId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ProviderException(
                $"Provider '{providerId}' returned '{track.Id}' for requested track '{trackId}'.");
        }

        return track;
    }
}
