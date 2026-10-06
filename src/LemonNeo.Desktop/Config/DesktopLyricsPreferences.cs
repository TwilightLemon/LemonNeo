namespace LemonNeo.Desktop.Config;

public sealed record DesktopLyricsPreferences(int LineCount = 2, double FontSize = 28,
    bool ShowRomanization = true, bool ShowTranslation = true, double WindowWidth = 760, double WindowHeight = 300);
