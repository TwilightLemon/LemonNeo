using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LemonNeo.Desktop.Controls.Playback;

public sealed class NowPlayingIndicator : Control
{
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<NowPlayingIndicator, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<double> PhaseProperty =
        AvaloniaProperty.Register<NowPlayingIndicator, double>(nameof(Phase));

    static NowPlayingIndicator()
    {
        AffectsRender<NowPlayingIndicator>(FillProperty, PhaseProperty);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public double Phase
    {
        get => GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Fill is not { } fill) return;

        const double radius = 11;
        using (context.PushOpacity(.13))
            context.DrawRectangle(fill, null, new Rect(Bounds.Size), radius, radius);

        var angle = Math.Tau * Phase;
        DrawBar(context, fill, 4.5, 8 + 3.5 * Math.Sin(angle) + Math.Sin(2 * angle + .4));
        DrawBar(context, fill, 9.5, 10 + 4.5 * Math.Sin(angle + 1.9) + Math.Sin(2 * angle - .7));
        DrawBar(context, fill, 14.5, 8.5 + 3.5 * Math.Sin(angle - .9) + Math.Sin(2 * angle + 1.4));
    }

    private void DrawBar(DrawingContext context, IBrush fill, double x, double height)
    {
        const double width = 3;
        var bottom = Bounds.Height - 4;
        context.DrawRectangle(fill, null, new Rect(x, bottom - height, width, height), width / 2, width / 2);
    }
}
