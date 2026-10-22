using Avalonia.Input;
using LemonNeo.Abstractions.Settings;

namespace LemonNeo.Desktop.Config;

/// <summary>Desktop owns entity defaults and validation; the host owns loading and persistence.</summary>
public static class DesktopPreferences
{
    public static void Register(ISettingsScope settings)
    {
        settings.Register(nameof(DesktopStoragePreferences), new DesktopStoragePreferences(), value => value with
        {
            LibraryDirectory = NormalizeOptionalPath(value.LibraryDirectory),
            CacheDirectory = NormalizeOptionalPath(value.CacheDirectory)
        });
        settings.Register(nameof(LibraryPreferences), new LibraryPreferences(), value => value with
        {
            PlaylistOrder = (value.PlaylistOrder ?? []).Distinct().ToArray()
        });
        settings.Register(nameof(AppearancePreferences), new AppearancePreferences(), value => value with
        {
            Theme = Enum.IsDefined(value.Theme) ? value.Theme : ThemePreference.System,
            ThemeColor = Enum.IsDefined(value.ThemeColor) ? value.ThemeColor : ThemeColorPreset.Classic
        });
        settings.Register(nameof(VisualEffectsPreferences), new VisualEffectsPreferences(), value => value with
        {
            AudioVisualizerStripCount = Math.Clamp(value.AudioVisualizerStripCount, 2, 256),
            AppleMusicMeshPresetSlot = Math.Clamp(value.AppleMusicMeshPresetSlot, -1, 4),
            AppleMusicMeshFrameRateLimit = NormalizeFrameRate(value.AppleMusicMeshFrameRateLimit),
            AppleMusicMeshRenderScale = NormalizeScale(value.AppleMusicMeshRenderScale, .125, 1, .4),
            AppleMusicMeshRotationScale = NormalizeScale(value.AppleMusicMeshRotationScale, 0, 2, 1),
            AppleMusicMeshBassPulseScale = NormalizeScale(value.AppleMusicMeshBassPulseScale, 0, 2, .2),
            AppleMusicMeshBlurScale = NormalizeScale(value.AppleMusicMeshBlurScale, 0, 3, 1.6)
        });
        settings.Register(nameof(LanguagePreferences), new LanguagePreferences(), value => value with
        {
            Language = Enum.IsDefined(value.Language) ? value.Language : LanguagePreference.System
        });
        settings.Register(nameof(LyricsPreferences), new LyricsPreferences(), value => value with
        {
            FontSize = double.IsFinite(value.FontSize) ? Math.Clamp(value.FontSize, 16, 100) : 24,
            Alignment = Enum.IsDefined(value.Alignment) ? value.Alignment : LyricAlignment.Center,
            LyricFontFamilies = value.LyricFontFamilies ?? ""
        });
        settings.Register(nameof(DesktopLyricsPreferences), new DesktopLyricsPreferences(), value => value with
        {
            LineCount = Math.Clamp(value.LineCount, 1, 2),
            FontSize = double.IsFinite(value.FontSize) ? Math.Clamp(value.FontSize, 16, 100) : 28,
            WindowWidth = double.IsFinite(value.WindowWidth) ? Math.Clamp(value.WindowWidth, 110, 3000) : 760,
            WindowHeight = double.IsFinite(value.WindowHeight) ? Math.Clamp(value.WindowHeight, 110, 3000) : 300,
            DisplayMode = Enum.IsDefined(value.DisplayMode) ? value.DisplayMode : LyricDisplayMode.Horizontal,
            LyricFontFamilies = value.LyricFontFamilies ?? ""
        });
        settings.Register(nameof(KeyboardShortcutPreferences), new KeyboardShortcutPreferences(),
            value => value with { Bindings = NormalizeBindings(value.Bindings) });
    }

    private static IReadOnlyDictionary<string, string> NormalizeBindings(
        IReadOnlyDictionary<string, string>? bindings)
    {
        var result = new Dictionary<string, string>();
        if (bindings is null) return result;
        foreach (var (id, value) in bindings)
        {
            if (string.IsNullOrEmpty(value))
            {
                // Empty records an explicit "disabled" choice.
                result[id] = "";
                continue;
            }

            try
            {
                _ = KeyGesture.Parse(value);
                result[id] = value;
            }
            catch (FormatException)
            {
                // Drop entries that no longer parse instead of failing startup.
            }
            catch (ArgumentException)
            {
                // Drop entries that no longer parse instead of failing startup.
            }
        }

        return result;
    }

    private static string? NormalizeOptionalPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
    }

    private static int NormalizeFrameRate(int value)
    {
        return value is -1 or 30 or 60 or 120 or 144 or 240 ? value : 120;
    }

    private static double NormalizeScale(double value, double minimum, double maximum, double fallback)
    {
        return double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
    }
}
