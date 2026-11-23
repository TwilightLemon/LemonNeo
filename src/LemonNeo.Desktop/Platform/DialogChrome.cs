using Avalonia;

namespace LemonNeo.Desktop.Platform;

public static class DialogChrome
{
    public static bool ExtendClientArea => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    // macOS keeps its native caption buttons when the extended title bar material has zero height.
    public static double TitleBarHeight => OperatingSystem.IsMacOS() ? 0 : 30;

    public static Thickness ContentPadding => ExtendClientArea ? new Thickness(0, 12, 0, 0) : default;
}
