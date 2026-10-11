using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;

namespace LemonNeo.Application.Playback;

/// <summary>The resource and identity actually selected for playback.</summary>
/// <param name="ProviderId">The resolving provider, or null when its provenance is unknown (e.g. cached audio).</param>
public sealed record PlaybackSourceDetails(
    PlaybackSource Source,
    string? ProviderId,
    ExternalMusicId SourceId,
    bool IsCached);