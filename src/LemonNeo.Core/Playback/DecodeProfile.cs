using System.Collections.Immutable;

namespace LemonNeo.Core.Playback;

/// <summary>Playback speed and equalizer gains in dB, ordered by BandFrequencies.</summary>
public sealed record DecodeProfile
{
    public const double MinimumSpeed = 0.5;
    public const double MaximumSpeed = 2;
    public static ImmutableArray<int> BandFrequencies { get; } = [100, 300, 1000, 3000, 10000];
    public static ImmutableArray<double> Flat { get; } = [0, 0, 0, 0, 0];
    public double Speed { get; init; } = 1;
    public ImmutableArray<double> Gains { get; init; } = Flat;

    public DecodeProfile Normalize() => this with
    {
        Speed = double.IsFinite(Speed) ? Math.Clamp(Speed, MinimumSpeed, MaximumSpeed) : 1,
        Gains = NormalizeGains(Gains)
    };

    private static ImmutableArray<double> NormalizeGains(ImmutableArray<double> gains)
    {
        if (gains.IsDefault || gains.Length != BandFrequencies.Length) return Flat;
        if (gains.All(gain => gain is >= -12 and <= 12)) return gains;
        return [.. gains.Select(gain => double.IsFinite(gain) ? Math.Clamp(gain, -12, 12) : 0)];
    }
}
