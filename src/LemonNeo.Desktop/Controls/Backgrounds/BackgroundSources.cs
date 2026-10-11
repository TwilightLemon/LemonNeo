namespace LemonNeo.Desktop.Controls.Backgrounds;

/// <summary>Encoded artwork passed to a dynamic background renderer.</summary>
public sealed record BackgroundArtworkFrame(string Id, ReadOnlyMemory<byte> EncodedData)
{
    public BackgroundArtworkFrame(string id, byte[] encodedData)
        : this(id, new ReadOnlyMemory<byte>(encodedData ?? throw new ArgumentNullException(nameof(encodedData))))
    {
    }
}

/// <summary>Supplies the current artwork and notifies controls when it changes.</summary>
public interface IBackgroundArtworkSource
{
    event EventHandler? ArtworkChanged;

    BackgroundArtworkFrame? CurrentArtwork { get; }
}

/// <summary>Supplies a normalized low-frequency response in the range 0..1.</summary>
public interface ILowFrequencyPulseSource
{
    bool TryGetLowFrequencyPulse(out float pulse);
}

public sealed class BackgroundArtworkFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception ?? throw new ArgumentNullException(nameof(exception));
}
