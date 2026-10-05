using Avalonia.Controls;
using Avalonia.Media;

namespace LemonNeo.Desktop.Infrastructure;

/// <summary>Stable brush instances let all views, including open dialogs, transition together.</summary>
public sealed class ThemeResources : ResourceDictionary
{
    private readonly Dictionary<string, SolidColorBrush> _brushes = [];
    private readonly GradientStop _glowStart = new() { Offset = 0 };
    private readonly GradientStop _glowEnd = new() { Offset = 1 };
    private readonly SolidColorBrush _playbackSurface = new() { Opacity = .78 };

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
            Center = new(0, 1, Avalonia.RelativeUnit.Relative),
            GradientOrigin = new(0, 1, Avalonia.RelativeUnit.Relative),
            RadiusX = new(.9, Avalonia.RelativeUnit.Relative), RadiusY = new(.9, Avalonia.RelativeUnit.Relative),
            GradientStops = [_glowStart, _glowEnd]
        };
        Apply(Snapshot());
    }

    public IReadOnlyDictionary<string, Color> Snapshot() => _brushes.ToDictionary(p => p.Key, p => p.Value.Color);

    public void Apply(IReadOnlyDictionary<string, Color> colors)
    {
        foreach (var (role, color) in colors) _brushes[role].Color = color;
        _playbackSurface.Color = colors["Surface"];
        var glow = colors["Accent"];
        _glowStart.Color = Color.FromArgb(30, glow.R, glow.G, glow.B);
        _glowEnd.Color = Color.FromArgb(0, glow.R, glow.G, glow.B);
    }
}
