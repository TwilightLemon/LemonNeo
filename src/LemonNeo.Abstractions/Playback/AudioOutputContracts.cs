using System.Text.Json.Serialization;

namespace LemonNeo.Abstractions.Playback;

[JsonConverter(typeof(JsonStringEnumConverter<AudioOutputBackend>))]
public enum AudioOutputBackend
{
    Default,
    Wasapi,
    Asio
}

public sealed record AudioOutputDevice(string Id, string Name);

public sealed record AudioOutputConfiguration(
    AudioOutputBackend Backend = AudioOutputBackend.Default,
    string? DeviceId = null,
    int? SampleRate = null,
    bool Exclusive = false);

/// <summary>Optional player provider capability for selecting audio output. Providers supporting live switching apply it to the active stream.</summary>
public interface IAudioOutputConfigurable
{
    IReadOnlyList<AudioOutputBackend> OutputBackends { get; }
    AudioOutputConfiguration OutputConfiguration { get; }
    IReadOnlyList<AudioOutputDevice> GetOutputDevices(AudioOutputBackend backend);
    void SetOutputConfiguration(AudioOutputConfiguration configuration);
}
