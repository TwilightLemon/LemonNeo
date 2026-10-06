using Avalonia.Media;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Controls.Lyrics;
using LemonNeo.Desktop.Controls.Shared;

namespace LemonNeo.Desktop.Utils;

public sealed record ThemeColorDefinition(ThemeColorPreset Id, string NameKey, Color Accent, Color DarkBase, Color LightBase,
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

    public static ThemeColorDefinition Get(ThemeColorPreset id) => BuiltIn.FirstOrDefault(p => p.Id == id) ?? BuiltIn[0];

    private static ThemeColorDefinition Define(ThemeColorPreset id, string accent, string dark, string light) =>
        new(id, $"ThemeColor{id}", Color.Parse(accent), Color.Parse(dark), Color.Parse(light));

    public static ThemeColorDefinition FromArtwork(Color color)
    {
        var hsl = color.ToHsl();
        // Black/white covers stay neutral. Highly saturated art never becomes a neon UI.
        var saturation = Math.Min(hsl.S, .58);
        return new(ThemeColorPreset.Classic, "ThemeColorArtwork",
            HslColor.ToRgb(hsl.H, saturation, Math.Clamp(hsl.L, .56, .72)),
            HslColor.ToRgb(hsl.H, saturation * .35, .12),
            HslColor.ToRgb(hsl.H, saturation * .22, .90), IsArtwork: true);
    }

    public static IReadOnlyDictionary<string, Color> Create(ThemeColorDefinition definition, bool dark)
    {
        var seed = definition.Accent;
        var hsl = seed.ToHsl();
        // Background tint is deliberately subtle, even for bright reference colors.
        var background = dark
            ? HslColor.ToRgb(definition.DarkBase.ToHsl().H, Math.Min(definition.DarkBase.ToHsl().S, .28), .095)
            : Mix(definition.LightBase, Colors.White, .90);
        var sidebar = dark ? Mix(background, Colors.Black, .16) : Mix(definition.LightBase, Colors.White, .80);
        var surface = dark ? Mix(background, Colors.White, .035) : Mix(background, Colors.White, .75);
        var raised = dark ? Mix(background, Colors.White, .075) : Mix(definition.LightBase, Colors.White, .86);
        var accent = HslColor.ToRgb(hsl.H, Math.Min(hsl.S, .65), dark ? Math.Clamp(hsl.L, .65, .76) : Math.Clamp(hsl.L, .57, .73));
        var soft = Mix(raised, seed, dark ? .13 : .16);
        var hover = Mix(raised, seed, dark ? .07 : .08);

        if (definition.Id == ThemeColorPreset.Classic && !definition.IsArtwork)
        {
            // Keep the existing default's recognizable lime and olive surfaces.
            background = Color.Parse(dark ? "#171A15" : "#FBFBFB");
            sidebar = Color.Parse(dark ? "#12150F" : "#EDEFE7");
            surface = Color.Parse(dark ? "#20241D" : "#FFFFFF");
            raised = Color.Parse(dark ? "#2A3024" : "#F0F2EA");
            accent = Color.Parse(dark ? "#DCEC7E" : "#DAE96D");
            soft = Color.Parse(dark ? "#333D20" : "#E5ECCB");
            hover = Color.Parse(dark ? "#30372A" : "#E7EBDD");
        }

        var backgrounds = new[] { background, sidebar, surface, raised, soft, hover };
        var text = EnsureContrast(dark ? Color.Parse("#EEF1F4") : Color.Parse("#20252C"), backgrounds, 7, dark);
        // Leave headroom for the intermediate colors and hover opacity during transitions.
        var secondary = EnsureContrast(Mix(text, background, .32), backgrounds, 5.3, dark);
        var accentText = EnsureContrast(dark ? accent : HslColor.ToRgb(hsl.H, Math.Min(hsl.S, .60), .32), backgrounds, 5.5, dark);
        var onAccent = Color.Parse("#172027");
        // Blue/purple can be dark at the same HSL lightness as yellow. Lift the fill itself
        // so button labels retain a stable dark foreground throughout palette transitions.
        accent = EnsureContrast(accent, [onAccent], 5.5, true);
        var dangerSoft = Color.Parse(dark ? "#442B28" : "#FAE9E8");
        var danger = EnsureContrast(Color.Parse(dark ? "#FFAB9F" : "#B33232"), [.. backgrounds, dangerSoft], 4.5, dark);
        return new Dictionary<string, Color>
        {
            ["Canvas"] = background, ["Sidebar"] = sidebar, ["SidebarTranslucent"] = sidebar,
            ["Surface"] = surface, ["SurfaceRaised"] = raised, ["Text"] = text,
            ["TextSecondary"] = secondary, ["Border"] = Mix(raised, text, .16),
            ["Accent"] = accent, ["OnAccent"] = onAccent, ["AccentText"] = accentText,
            ["LyricText"] = secondary, ["LyricHighlight"] = accentText,
            ["AccentSoft"] = soft, ["Hover"] = hover, ["Danger"] = danger,
            ["DangerSoft"] = dangerSoft, ["Focus"] = accentText,
            ["Artwork"] = Mix(raised, seed, .20)
        };
    }

    public static Color Mix(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte Channel(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return Color.FromArgb(Channel(from.A, to.A), Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));
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
