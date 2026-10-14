namespace LemonNeo.Desktop.Config;

public enum LyricDisplayMode
{
    Horizontal,
    Vertical
}

public sealed record DesktopLyricsPreferences(
    bool IsEnabled = false,
    int LineCount = 2,
    double FontSize = 28,
    bool ShowRomanization = true,
    bool ShowTranslation = true,
    double WindowWidth = 760,
    double WindowHeight = 300,
    int? WindowX = null,
    int? WindowY = null,
    LyricDisplayMode DisplayMode = LyricDisplayMode.Horizontal,
    string LyricFontFamilies = "");
