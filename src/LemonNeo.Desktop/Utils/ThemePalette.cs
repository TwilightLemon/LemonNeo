using Avalonia.Media;
using LemonNeo.Desktop.Config;

namespace LemonNeo.Desktop.Utils;

public sealed record ThemeColorDefinition(
    ThemeColorPreset Id,
    string NameKey,
    Color Accent,
    Color DarkBase,
    Color LightBase,
    bool IsArtwork = false);

/// <summary>Reference colors describe a mood; semantic UI colors are adapted to each brightness mode.</summary>
public static class ThemePalette
{
    public static IReadOnlyList<ThemeColorDefinition> BuiltIn { get; } = Array.AsReadOnly(new[]
    {
        Define(ThemeColorPreset.Classic, "#DCEC7E", "#171A15", "#EDEFE7"),
        Define(ThemeColorPreset.PolarRose, "#C36F8B", "#111E30", "#9AA4B0"),
        Define(ThemeColorPreset.Basalt, "#919AA4", "#1F2226", "#E6E8E5"),
        Define(ThemeColorPreset.MoonGold, "#D2A663", "#4E6584", "#E4DFD2"),
        Define(ThemeColorPreset.Aurora, "#37CCA3", "#0D405B", "#D7E3E5"),
        Define(ThemeColorPreset.IceBlue, "#79C3DC", "#92B0BC", "#D3E0E5"),
        Define(ThemeColorPreset.PolarNight, "#BDCDDC", "#0D2349", "#768294"),
        Define(ThemeColorPreset.IceCyan, "#2FBBD2", "#0C2B4E", "#DEEAF0"),
        Define(ThemeColorPreset.Twilight, "#977DBF", "#96A7C9", "#D6AAC2")
    });

    public static ThemeColorDefinition Get(ThemeColorPreset id)
    {
        return BuiltIn.FirstOrDefault(p => p.Id == id) ?? BuiltIn[0];
    }

    private static ThemeColorDefinition Define(ThemeColorPreset id, string accent, string dark, string light)
    {
        return new ThemeColorDefinition(id, $"ThemeColor{id}", Color.Parse(accent), Color.Parse(dark),
            Color.Parse(light));
    }

    public static ThemeColorDefinition FromArtwork(Color color)
    {
        var hsl = color.ToHsl();
        // Black/white covers stay neutral. Highly saturated art never becomes a neon UI.
        var saturation = Math.Min(hsl.S, .58);
        return new ThemeColorDefinition(ThemeColorPreset.Classic, "ThemeColorArtwork",
            HslColor.ToRgb(hsl.H, saturation, Math.Clamp(hsl.L, .56, .72)),
            HslColor.ToRgb(hsl.H, saturation * .35, .12),
            HslColor.ToRgb(hsl.H, saturation * .22, .90), true);
    }

    public static IReadOnlyDictionary<string, Color> Create(ThemeColorDefinition definition, bool dark)
    {
        var hsl = definition.Accent.ToHsl();
        var darkBase = definition.DarkBase.ToHsl();
        var lightBase = definition.LightBase.ToHsl();
        // Compress source lightness into a light range while retaining differences between presets.
        var lightTint = HslColor.ToRgb(lightBase.H, Math.Min(lightBase.S, .32), .81 + .10 * lightBase.L);
        var background = dark
            ? HslColor.ToRgb(darkBase.H, Math.Min(darkBase.S, .28), .095)
            : Mix(lightTint, Colors.White, .68);
        var sidebar = dark ? Mix(background, Colors.Black, .16) : Mix(lightTint, Colors.White, .25);
        var surface = dark ? Mix(background, Colors.White, .035) : Mix(lightTint, Colors.White, .92);
        var raised = dark ? Mix(background, Colors.White, .075) : Mix(lightTint, Colors.White, .45);
        var accentBase = HslColor.ToRgb(hsl.H, Math.Min(hsl.S, .65),
            dark ? Math.Clamp(hsl.L, .65, .76) : Math.Clamp(hsl.L, .57, .73));
        var onAccent = Color.Parse("#172027");
        // Blue/purple can be dark at the same HSL lightness as yellow. Lift the fill itself
        // so button labels retain a stable dark foreground throughout palette transitions.
        var accent = EnsureContrast(accentBase, [onAccent], 5.5, true);
        var tintAccent = dark ? definition.Accent : accent;
        var tintHsl = tintAccent.ToHsl();
        var soft = Mix(raised, tintAccent, dark ? .13 : .22);
        var hover = Mix(raised, tintAccent, dark ? .07 : .08);

        var backgrounds = new[] { background, sidebar, surface, raised, soft, hover };
        var text = EnsureContrast(dark ? Color.Parse("#EEF1F4") : Color.Parse("#20252C"), backgrounds, 7, dark);
        // Leave headroom for the intermediate colors and hover opacity during transitions.
        var secondary = Color.Parse(dark ? "#C0FEFEFE" : "#C0000000");
        var accentText = EnsureContrast(dark ? accentBase : HslColor.ToRgb(tintHsl.H, Math.Min(tintHsl.S, .60), .32),
            backgrounds, 5.5, dark);
        var lyricHighlighter = dark
            ? HslColor.ToRgb(hsl.H, hsl.S, .94)
            : EnsureContrast(HslColor.ToRgb(tintHsl.H, Math.Min(tintHsl.S, .35), .12), backgrounds, 7, false);
        var lyricText = Color.Parse(dark ? "#90FEFEFE" : "#90000000");
        var playbackSurface = Color.Parse(dark ? "#4E000000" : "#4EEFEFEF");
        var border = Color.Parse(dark ? "#2EFFFFFF" : "#2E000000");
        var dangerSoft = Color.Parse(dark ? "#442B28" : "#FAE9E8");
        var danger = EnsureContrast(Color.Parse(dark ? "#FFAB9F" : "#B33232"), [.. backgrounds, dangerSoft], 4.5, dark);
        return new Dictionary<string, Color>
        {
            ["Canvas"] = background, ["Sidebar"] = sidebar, ["SidebarTranslucent"] = sidebar,
            ["Surface"] = surface, ["SurfaceRaised"] = raised, ["DialogTint"] = raised, ["Text"] = text,
            ["TextSecondary"] = secondary, ["Border"] = border,
            ["Accent"] = accent, ["OnAccent"] = onAccent, ["AccentText"] = accentText,
            ["LyricText"] = lyricText, ["LyricHighlight"] = lyricHighlighter,
            ["AccentSoft"] = soft, ["Hover"] = hover, ["Danger"] = danger,
            ["DangerSoft"] = dangerSoft, ["Focus"] = accentText,
            ["PlaybackSurface"] = playbackSurface,
            ["Artwork"] = Mix(raised, tintAccent, .20)
        };
    }

    public static Color Mix(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);

        byte Channel(byte a, byte b)
        {
            return (byte)Math.Round(a + (b - a) * amount);
        }

        return Color.FromArgb(Channel(from.A, to.A), Channel(from.R, to.R), Channel(from.G, to.G),
            Channel(from.B, to.B));
    }

    public static double Contrast(Color first, Color second)
    {
        static double Luminance(Color c)
        {
            static double Linear(byte channel)
            {
                var value = channel / 255d;
                return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
            }

            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }

        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static Color EnsureContrast(Color color, Color[] backgrounds, double minimum, bool lighten)
    {
        if (backgrounds.All(b => Contrast(color, b) >= minimum)) return color;
        var end = lighten ? Colors.White : Colors.Black;
        var low = 0d;
        var high = 1d;
        for (var i = 0; i < 24; i++)
        {
            var mid = (low + high) / 2;
            if (backgrounds.All(b => Contrast(Mix(color, end, mid), b) >= minimum)) high = mid;
            else low = mid;
        }

        return Mix(color, end, high);
    }
}
