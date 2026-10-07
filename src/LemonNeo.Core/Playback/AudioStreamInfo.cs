namespace LemonNeo.Core.Playback;

/// <summary>Measured properties of the encoded audio, not the decoder's output format.</summary>
public sealed record AudioStreamInfo(
    int? BitrateKbps = null,
    int? SampleRateHz = null,
    int? BitsPerSample = null,
    int? Channels = null,
    bool? IsLossless = null,
    string? Codec = null);
