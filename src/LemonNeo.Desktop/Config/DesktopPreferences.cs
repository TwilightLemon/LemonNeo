using LemonNeo.Abstractions.Settings;

namespace LemonNeo.Desktop.Config;

/// <summary>Desktop owns entity defaults and validation; the host owns loading and persistence.</summary>
public static class DesktopPreferences
{
    public static void Register(ISettingsScope settings)
    {
        settings.Register(nameof(AppearancePreferences), new AppearancePreferences(), value => value with
        {
            Theme = Enum.IsDefined(value.Theme) ? value.Theme : ThemePreference.System,
            ThemeColor = Enum.IsDefined(value.ThemeColor) ? value.ThemeColor : ThemeColorPreset.Classic
        });
        settings.Register(nameof(LanguagePreferences), new LanguagePreferences(), value => value with
        {
            Language = Enum.IsDefined(value.Language) ? value.Language : LanguagePreference.System
        });
        settings.Register(nameof(LyricsPreferences), new LyricsPreferences(), value => value with
        {
            FontSize = double.IsFinite(value.FontSize) ? Math.Clamp(value.FontSize, 16, 48) : 24,
            Alignment = Enum.IsDefined(value.Alignment) ? value.Alignment : LyricAlignment.Center
        });
        settings.Register(nameof(DesktopLyricsPreferences), new DesktopLyricsPreferences(), value => value with
        {
            LineCount = Math.Clamp(value.LineCount, 1, 2),
            FontSize = double.IsFinite(value.FontSize) ? Math.Clamp(value.FontSize, 16, 48) : 28,
            WindowWidth = double.IsFinite(value.WindowWidth) ? Math.Clamp(value.WindowWidth, 320, 3000) : 760,
            WindowHeight = double.IsFinite(value.WindowHeight) ? Math.Clamp(value.WindowHeight, 110, 1200) : 300
        });
    }
}
