using System.Diagnostics;

namespace LemonNeo.Desktop.Controls.Backgrounds;

/// <summary>
/// Converts FFT magnitudes into a bass-dominant, transient-aware pulse suitable
/// for driving artwork scale. The bands and response curve follow the modern
/// Lyricify Backgrounds implementation while accepting an existing FFT source.
/// </summary>
public sealed class LowFrequencyPulseAnalyzer
{
    private const float SilenceFloorDecibels = -72f;
    private const float BassLevelFloorDecibels = -50f;
    private const float BassLevelCeilingDecibels = -18f;
    private const float BassDominanceFloorDecibels = 0f;
    private const float BassDominanceCeilingDecibels = 8f;
    private const float BassRiseFloorDecibels = 1.2f;
    private const float BassRiseCeilingDecibels = 7f;
    private const float SharpAttackLevelFloorDecibels = -45f;
    private const float SharpAttackRiseFloorDecibels = 7f;
    private const float SharpAttackRiseCeilingDecibels = 14f;
    private const float SharpAttackReleaseSeconds = .09f;
    private const float HarmonicBassConfidenceFloor = .12f;
    private const float HarmonicBassConfidenceCeiling = .3f;
    private const float HarmonicBassAttackBoost = .9f;
    private const float SustainedBassResponse = .1f;
    private const float FeatureBaselineAttackSeconds = 1.1f;
    private const float FeatureBaselineReleaseSeconds = .16f;

    private BassFeatureState _state;
    private long _lastTimestamp;

    public float Process(ReadOnlySpan<float> fftMagnitudes, int sampleRateHz)
    {
        if (fftMagnitudes.Length < 2 || sampleRateHz <= 0) return 0f;

        var bassPower = Math.Max(
            GetAverageBandPower(fftMagnitudes, sampleRateHz, 30f, 105f),
            GetAverageBandPower(fftMagnitudes, sampleRateHz, 75f, 155f) * .9d);
        var upperBassPower = GetAverageBandPower(fftMagnitudes, sampleRateHz, 145f, 210f);
        bassPower += Math.Min(upperBassPower, bassPower * 1.35d) * .2d;

        var referencePower = Math.Max(
            GetAverageBandPower(fftMagnitudes, sampleRateHz, 155f, 380f) * 2.3d,
            GetAverageBandPower(fftMagnitudes, sampleRateHz, 380f, 760f) * 1.6d);

        var now = Stopwatch.GetTimestamp();
        var seconds = _lastTimestamp == 0
            ? 1f / 60f
            : (float)Math.Clamp((now - _lastTimestamp) / (double)Stopwatch.Frequency, 1d / 240d, .25d);
        _lastTimestamp = now;
        return GetBassResponse(bassPower, referencePower, seconds);
    }

    public void Reset()
    {
        _state = default;
        _lastTimestamp = 0;
    }

    private float GetBassResponse(double bassPower, double referencePower, float seconds)
    {
        var bassDecibels = PowerToDecibels(bassPower);
        var referenceDecibels = PowerToDecibels(referencePower);
        if (!_state.Initialized)
        {
            _state.SlowBassDecibels = MathF.Max(SilenceFloorDecibels,
                bassDecibels - BassRiseCeilingDecibels);
            _state.SlowReferenceDecibels = referenceDecibels;
            _state.PreviousBassDecibels = bassDecibels;
            _state.Initialized = true;
        }

        var frameBassRise = MathF.Max(0f, bassDecibels - _state.PreviousBassDecibels);
        _state.PreviousBassDecibels = bassDecibels;
        var bassRise = MathF.Max(0f, bassDecibels - _state.SlowBassDecibels);
        var referenceRise = MathF.Max(0f, referenceDecibels - _state.SlowReferenceDecibels);
        var dominance = SmoothRange(bassDecibels - referenceDecibels,
            BassDominanceFloorDecibels, BassDominanceCeilingDecibels);

        var sharpAttackTarget = bassDecibels >= SharpAttackLevelFloorDecibels
            ? SmoothRange(frameBassRise, SharpAttackRiseFloorDecibels, SharpAttackRiseCeilingDecibels)
            : 0f;
        var sharpAttackDecay = MathF.Exp(-seconds / SharpAttackReleaseSeconds);
        _state.SharpAttack = MathF.Max(sharpAttackTarget, _state.SharpAttack * sharpAttackDecay);

        var harmonicConfidence = SmoothRange(dominance,
            HarmonicBassConfidenceFloor, HarmonicBassConfidenceCeiling) *
            _state.SharpAttack * HarmonicBassAttackBoost;
        var bassConfidence = MathF.Max(dominance, harmonicConfidence);
        var bassOnlyRise = bassRise - referenceRise * (.7f - dominance * .35f);

        _state.SlowBassDecibels = SmoothBaseline(_state.SlowBassDecibels, bassDecibels, seconds);
        _state.SlowReferenceDecibels = SmoothBaseline(_state.SlowReferenceDecibels, referenceDecibels, seconds);

        var level = SmoothRange(bassDecibels, BassLevelFloorDecibels, BassLevelCeilingDecibels);
        var transient = SmoothRange(bassOnlyRise, BassRiseFloorDecibels, BassRiseCeilingDecibels);
        return Math.Clamp(level * bassConfidence *
            (SustainedBassResponse + (1f - SustainedBassResponse) * transient), 0f, 1f);
    }

    private static double GetAverageBandPower(ReadOnlySpan<float> fft, int sampleRateHz,
        float minimumHertz, float maximumHertz)
    {
        var fftLength = fft.Length * 2;
        var firstBin = Math.Clamp((int)MathF.Ceiling(minimumHertz * fftLength / sampleRateHz), 1, fft.Length - 1);
        var lastBin = Math.Clamp((int)MathF.Floor(maximumHertz * fftLength / sampleRateHz), firstBin,
            fft.Length - 1);
        double power = 0;
        for (var bin = firstBin; bin <= lastBin; bin++)
        {
            var magnitude = Math.Clamp(fft[bin], 0f, 4f);
            power += magnitude * magnitude;
        }

        return power / (lastBin - firstBin + 1);
    }

    private static float SmoothBaseline(float current, float target, float seconds)
    {
        var timeConstant = target > current ? FeatureBaselineAttackSeconds : FeatureBaselineReleaseSeconds;
        var mix = 1f - MathF.Exp(-seconds / timeConstant);
        return MathF.Max(SilenceFloorDecibels, current + (target - current) * mix);
    }

    private static float PowerToDecibels(double power)
    {
        return (float)(10d * Math.Log10(Math.Max(power, 1e-12d)));
    }

    private static float SmoothRange(float value, float floor, float ceiling)
    {
        var normalized = Math.Clamp((value - floor) / (ceiling - floor), 0f, 1f);
        return normalized * normalized * (3f - 2f * normalized);
    }

    private struct BassFeatureState
    {
        public float SlowBassDecibels;
        public float SlowReferenceDecibels;
        public float PreviousBassDecibels;
        public float SharpAttack;
        public bool Initialized;
    }
}
