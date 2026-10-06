namespace LemonNeo.Desktop.Config;

public enum ThemeColorPreset { Classic, PolarRose, Basalt, MoonGold, Aurora, IceBlue, PolarNight, IceCyan, Twilight }

public enum ThemePreference { System, Light, Dark }

public sealed record AppearancePreferences(ThemePreference Theme = ThemePreference.System,
    bool ReduceMotion = false, ThemeColorPreset ThemeColor = ThemeColorPreset.Classic, bool UseArtworkColors = false);
