namespace LemonNeo.Desktop.Controls.Playback;

public interface IAudioVisualizerSource
{
    int FftBinCount { get; }
    bool TryGetFftData(Span<float> destination);
}
