namespace LemonNeo.Desktop.Config;

public enum LyricAlignment { Center, Left }

public sealed record LyricsPreferences(double FontSize = 24, bool ShowRomanization = true,
    bool ShowTranslation = true, LyricAlignment Alignment = LyricAlignment.Center, bool Bold = false);
