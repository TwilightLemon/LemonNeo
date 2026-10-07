using Avalonia;
using Avalonia.Controls;

namespace LemonNeo.Desktop.Controls.Shared;

/// <summary>Applies track state to the item container so every highlight uses its full bounds.</summary>
public sealed class TrackRowAppearance : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsCurrentProperty =
        AvaloniaProperty.RegisterAttached<TrackRowAppearance, ListBoxItem, bool>("IsCurrent");
    public static readonly AttachedProperty<bool> IsMarkedProperty =
        AvaloniaProperty.RegisterAttached<TrackRowAppearance, ListBoxItem, bool>("IsMarked");

    static TrackRowAppearance()
    {
        IsCurrentProperty.Changed.AddClassHandler<ListBoxItem>((item, _) => item.Classes.Set("current", GetIsCurrent(item)));
        IsMarkedProperty.Changed.AddClassHandler<ListBoxItem>((item, _) => item.Classes.Set("marked", GetIsMarked(item)));
    }

    public static bool GetIsCurrent(ListBoxItem item) => item.GetValue(IsCurrentProperty);
    public static void SetIsCurrent(ListBoxItem item, bool value) => item.SetValue(IsCurrentProperty, value);
    public static bool GetIsMarked(ListBoxItem item) => item.GetValue(IsMarkedProperty);
    public static void SetIsMarked(ListBoxItem item, bool value) => item.SetValue(IsMarkedProperty, value);
}
