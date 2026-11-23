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
    private readonly LinearGradientBrush _playbackRow = new()
    {
        StartPoint = new RelativePoint(0, .5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, .5, RelativeUnit.Relative),
        GradientStops = [new GradientStop(default, 0), new GradientStop(default, .28), new GradientStop(default, .70)]
    };

    public ThemeResources()
    {
        foreach (var (role, color) in ThemePalette.Create(ThemePalette.Get(ThemeColorPreset.Classic), false))
        {
            var brush = new SolidColorBrush(color) { Opacity = role == "SidebarTranslucent" ? .85 : 1 };
            _brushes.Add(role, brush);
            this[$"Brush.{role}"] = brush;
        }
        this["Brush.PlaybackRow"] = _playbackRow;
        Apply(Snapshot());
    }

    public IReadOnlyDictionary<string, Color> Snapshot()
    {
        return _brushes.ToDictionary(p => p.Key, p => p.Value.Color);
    }

    public void Apply(IReadOnlyDictionary<string, Color> colors)
    {
        foreach (var (role, color) in colors) _brushes[role].Color = color;
        var accent = colors["AccentText"];
        _playbackRow.GradientStops[0].Color = Color.FromArgb(28, accent.R, accent.G, accent.B);
        _playbackRow.GradientStops[1].Color = Color.FromArgb(12, accent.R, accent.G, accent.B);
        _playbackRow.GradientStops[2].Color = Color.FromArgb(0, accent.R, accent.G, accent.B);
    }
}
