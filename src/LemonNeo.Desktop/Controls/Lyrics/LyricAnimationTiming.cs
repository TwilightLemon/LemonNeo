using System.Globalization;
using Avalonia.Animation.Easings;
using LemonNeo.Core.Lyrics;

namespace LemonNeo.Desktop.Controls.Lyrics;

/// <summary>Presentation-only timing; the provider's words and timestamps remain intact.</summary>
internal readonly record struct LyricAnimationTiming(
    int Start, int Length, double StartTimeMs, double EndTimeMs, double WordEndTimeMs, bool IsEmphasized)
{
    public const double DefaultDurationMultiplier = 2;
    private static readonly CubicEaseOut LiftEasing = new();

    // Movement has its own clock: long words sweep across with staggered lifts,
    // while their highlight continues to follow the original per-character timing.
    public double LiftStartTimeMs { get; init; } = StartTimeMs;
    public double LiftDurationMs { get; init; } = EndTimeMs - StartTimeMs;

    public static LyricAnimationTiming[] Create(IReadOnlyList<LyricsWord> words, double multiplier)
    {
        var totalDuration = 0d;
        var timedCount = 0;
        foreach (var word in words)
        {
            if (word.EndTimeMs <= word.StartTimeMs || string.IsNullOrWhiteSpace(word.Text)) continue;
            totalDuration += (double)word.EndTimeMs - word.StartTimeMs;
            timedCount++;
        }

        // A whole word lifts over 1.5× the line's average word duration (at least 187 ms),
        // as in the WPF reference, so the lift stays connected across the whole line.
        var averageDuration = timedCount > 0 ? totalDuration / timedCount : 0;
        var liftUpDuration = Math.Max(187, averageDuration * 1.5);

        var result = new List<LyricAnimationTiming>();
        var offset = 0;
        foreach (var word in words)
        {
            var duration = (double)word.EndTimeMs - word.StartTimeMs;
            // Empty/untimed tokens are not sung peers. A lone word has no comparison baseline.
            var emphasized = timedCount > 1 && duration > 0 && !string.IsNullOrWhiteSpace(word.Text) &&
                             duration > multiplier * ((totalDuration - duration) / (timedCount - 1));
            if (emphasized)
            {
                // Text elements preserve surrogate pairs, combining marks and joined emoji.
                var starts = StringInfo.ParseCombiningCharacters(word.Text);
                // Reference the WPF lift-up wave: unit shapes both the per-character lift
                // duration (unit * p) and the stagger between neighbors, so the sweep reads
                // left to right and trails ~20% past the word's end.
                var unit = duration * 1.2 / starts.Length;
                var p = starts.Length * .6;
                var stagger = starts.Length > 1 ? unit - (p - 1) * unit / (starts.Length - 1) : 0;
                var liftDuration = unit * p;
                for (var i = 0; i < starts.Length; i++)
                {
                    var end = i + 1 < starts.Length ? starts[i + 1] : word.Text.Length;
                    result.Add(new LyricAnimationTiming(offset + starts[i], end - starts[i],
                        word.StartTimeMs + duration * i / starts.Length,
                        word.StartTimeMs + duration * (i + 1) / starts.Length, word.EndTimeMs, true)
                    {
                        LiftStartTimeMs = word.StartTimeMs + stagger * i,
                        LiftDurationMs = liftDuration
                    });
                }
            }
            else if (word.Text.Length > 0)
            {
                result.Add(new LyricAnimationTiming(offset, word.Text.Length, word.StartTimeMs,
                    word.EndTimeMs, word.EndTimeMs, false)
                {
                    LiftDurationMs = liftUpDuration
                });
            }

            offset += word.Text.Length;
        }

        return result.ToArray();
    }

    public double HighlightProgress(double position) => position < StartTimeMs ? 0
        : EndTimeMs <= StartTimeMs ? 1
        : Math.Clamp((position - StartTimeMs) / (EndTimeMs - StartTimeMs), 0, 1);

    public double LiftProgress(double position) => LiftEasing.Ease(
        Math.Clamp((position - LiftStartTimeMs) / LiftDurationMs, 0, 1));

    public double GlowProgress(double position) => IsEmphasized
        ? LiftProgress(position) * (1 - SmoothStep((position - WordEndTimeMs) / 450)) : 0;

    private static double SmoothStep(double value)
    {
        var progress = Math.Clamp(value, 0, 1);
        return progress * progress * (3 - 2 * progress);
    }
}
