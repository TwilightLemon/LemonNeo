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

    public ThemeResources()
    {
        foreach (var (role, color) in ThemePalette.Create(ThemePalette.Get(ThemeColorPreset.Classic), false))
        {
            var brush = new SolidColorBrush(color) { Opacity = role == "SidebarTranslucent" ? .85 : 1 };
            _brushes.Add(role, brush);
            this[$"Brush.{role}"] = brush;
        }
        Apply(Snapshot());
    }

    public IReadOnlyDictionary<string, Color> Snapshot()
    {
        return _brushes.ToDictionary(p => p.Key, p => p.Value.Color);
    }

    public void Apply(IReadOnlyDictionary<string, Color> colors)
    {
        foreach (var (role, color) in colors) _brushes[role].Color = color;
    }
}