using Avalonia.Media;
using LemonNeo.Desktop.Infrastructure;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class ThemePaletteTests
{
    [Fact]
    public void AllBuiltInPalettesHaveReadableTextInBothModes()
    {
        foreach (var definition in ThemePalette.BuiltIn)
        foreach (var dark in new[] { false, true }) AssertReadable(definition, dark);
    }

    [Fact]
    public void ExtremeAndRandomArtworkColorsRemainReadable()
    {
        var random = new Random(20261004);
        var colors = new[] { Colors.Black, Colors.White, Colors.Red, Colors.Lime, Colors.Blue, Colors.Magenta, Colors.Yellow, Colors.Gray }
            .Concat(Enumerable.Range(0, 500).Select(_ => Color.FromRgb((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256))));
        foreach (var color in colors)
        foreach (var dark in new[] { false, true }) AssertReadable(ThemePalette.FromArtwork(color), dark);
    }

    [Fact]
    public void TextRemainsReadableDuringBuiltInColorTransitions()
    {
        foreach (var dark in new[] { false, true })
        foreach (var source in ThemePalette.BuiltIn)
        foreach (var destination in ThemePalette.BuiltIn)
        foreach (var progress in new[] { .25, .5, .75 })
        {
            var from = ThemePalette.Create(source, dark);
            var to = ThemePalette.Create(destination, dark);
            var frame = to.ToDictionary(p => p.Key, p => ThemePalette.Mix(from[p.Key], p.Value, progress));
            foreach (var background in new[] { "Canvas", "Sidebar", "Surface", "SurfaceRaised", "AccentSoft", "Hover" })
            foreach (var foreground in new[] { "Text", "TextSecondary", "AccentText" })
                Assert.True(ThemePalette.Contrast(frame[foreground], frame[background]) >= 4.5,
                    $"{source.Id}->{destination.Id}, dark={dark}, progress={progress}: {foreground}/{background}");
            Assert.True(ThemePalette.Contrast(frame["OnAccent"], frame["Accent"]) >= 4.5,
                $"{source.Id}->{destination.Id}, dark={dark}, progress={progress}: button label");
        }
    }

    private static void AssertReadable(ThemeColorDefinition definition, bool dark)
    {
        var colors = ThemePalette.Create(definition, dark);
        foreach (var background in new[] { "Canvas", "Sidebar", "Surface", "SurfaceRaised", "AccentSoft", "Hover" })
        foreach (var (foreground, minimum) in new[] { ("Text", 7d), ("TextSecondary", 4.5), ("AccentText", 4.5), ("LyricText", 4.5), ("LyricHighlight", 4.5), ("Danger", 4.5) })
        {
            var contrast = ThemePalette.Contrast(colors[foreground], colors[background]);
            Assert.True(contrast >= minimum, $"{definition.NameKey}, dark={dark}: {foreground}/{background} contrast {contrast:F3} < {minimum}");
        }
        Assert.True(ThemePalette.Contrast(colors["OnAccent"], colors["Accent"]) >= 4.5,
            $"{definition.Accent}, dark={dark}: unreadable button label");
        Assert.True(ThemePalette.Contrast(colors["Danger"], colors["DangerSoft"]) >= 4.5);
        var canvas = colors["Canvas"];
        // Near-white HSL saturation is misleading; RGB chroma measures visible background tint.
        Assert.InRange(Math.Max(canvas.R, Math.Max(canvas.G, canvas.B)) - Math.Min(canvas.R, Math.Min(canvas.G, canvas.B)), 0, 18);
    }
}
