using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Utils;

namespace LemonNeo.Desktop.Resources.Themes;

/// <summary>Stable brush instances let all views, including open dialogs, transition together.</summary>
public sealed class ThemeResources : ResourceDictionary
{
    private readonly Dictionary<string, SolidColorBrush> _brushes = [];
    private readonly GradientStop _glowEnd = new() { Offset = 1 };
    private readonly GradientStop _glowStart = new() { Offset = 0 };
    private readonly SolidColorBrush _playbackSurface = new() { Opacity = .42 };

    public ThemeResources()
    {
        foreach (var (role, color) in ThemePalette.Create(ThemePalette.Get(ThemeColorPreset.Classic), false))
        {
            var brush = new SolidColorBrush(color) { Opacity = role == "SidebarTranslucent" ? .85 : 1 };
            _brushes.Add(role, brush);
            this[$"Brush.{role}"] = brush;
        }

        this["Brush.PlaybackSurface"] = _playbackSurface;
        this["Brush.NowPlayingGlow"] = new RadialGradientBrush
        {
            Center = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0, 1, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(.9, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(.9, RelativeUnit.Relative),
            GradientStops = [_glowStart, _glowEnd]
        };
        Apply(Snapshot());
    }

    public IReadOnlyDictionary<string, Color> Snapshot()
    {
        return _brushes.ToDictionary(p => p.Key, p => p.Value.Color);
    }

    public void Apply(IReadOnlyDictionary<string, Color> colors)
    {
        foreach (var (role, color) in colors) _brushes[role].Color = color;
        _playbackSurface.Color = colors["Surface"];
        var glow = colors["Accent"];
        _glowStart.Color = Color.FromArgb(30, glow.R, glow.G, glow.B);
        _glowEnd.Color = Color.FromArgb(0, glow.R, glow.G, glow.B);
    }
}