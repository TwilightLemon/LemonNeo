using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LemonNeo.Desktop.Controls.Shared;

internal static class PageBottomFade
{
    public static void Attach(Control control)
    {
        var mask = new LinearGradientBrush
        {
            GradientStops = [new GradientStop(Colors.White, 0), new GradientStop(Colors.Transparent, 1)]
        };
        control.OpacityMask = mask;
        Update(control.Bounds.Height);
        control.SizeChanged += (_, args) => Update(args.NewSize.Height);

        void Update(double height)
        {
            mask.StartPoint = new RelativePoint(0, Math.Max(0, height - 64), RelativeUnit.Absolute);
            mask.EndPoint = new RelativePoint(0, height, RelativeUnit.Absolute);
        }
    }
}
