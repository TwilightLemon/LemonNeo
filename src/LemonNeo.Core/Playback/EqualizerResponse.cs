using System.Collections.Immutable;

namespace LemonNeo.Core.Playback;

/// <summary>Magnitude response of the overlapping constant-Q peaking filters in a decode profile.</summary>
public sealed class EqualizerResponse
{
    public const float QualityFactor = 0.9f;
    public const int ReferenceSampleRate = 48000;
    private readonly int _sampleRate;
    private readonly Band[] _bands;

    public EqualizerResponse(ImmutableArray<double> gains, int sampleRate)
    {
        _sampleRate = sampleRate > 0 ? sampleRate : ReferenceSampleRate;
        var bands = new List<Band>();
        for (var i = 0; !gains.IsDefault && i < Math.Min(gains.Length, DecodeProfile.BandFrequencies.Length); i++)
        {
            var center = DecodeProfile.BandFrequencies[i];
            if (center >= _sampleRate / 2d) break;
            if (gains[i] == 0) continue;
            var omega = Math.Tau * center / _sampleRate;
            var alpha = Math.Sin(omega) / (2 * QualityFactor);
            var amplitude = Math.Pow(10, gains[i] / 40);
            bands.Add(new(Math.Cos(omega), alpha * amplitude, alpha / amplitude));
        }
        _bands = bands.ToArray();
    }

    public double GainAt(double frequency)
    {
        if (frequency <= 0 || frequency >= _sampleRate / 2d) return 0;
        var (sin, cos) = Math.SinCos(Math.Tau * frequency / _sampleRate);
        double gain = 0;
        foreach (var band in _bands)
        {
            // Evaluate the peaking biquad on the unit circle. Cascade magnitudes add in dB.
            var real = cos - band.CenterCos;
            var numerator = sin * band.Numerator;
            var denominator = sin * band.Denominator;
            gain += 10 * Math.Log10((real * real + numerator * numerator) /
                                    (real * real + denominator * denominator));
        }
        return gain;
    }

    private readonly record struct Band(double CenterCos, double Numerator, double Denominator);
}
