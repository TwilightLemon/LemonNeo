using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;

namespace LemonNeo.Desktop.Controls.Navigation;

/// <summary>Hides the old page immediately and animates the new page, reversing travel for pop.</summary>
public sealed class SlideFadeTransition : IPageTransition
{
    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(220);
    public double Distance { get; set; } = 32;
    public Easing EntranceEasing { get; } = new CubicEaseOut();

    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;
        if (from is not null) from.IsVisible = false;
        if (to is null) return;
        var toTransform = to.RenderTransform;
        var toOpacity = to.Opacity;
        try
        {
            to.IsVisible = true;
            if (Duration > TimeSpan.Zero)
            {
                to.RenderTransform = new TranslateTransform();
                await CreateAnimation(forward ? Distance : -Distance, toOpacity).RunAsync(to, cancellationToken);
            }
        }
        finally
        {
            // Cached pages must be reusable after cancellation and repeated back/forward navigation.
            to.IsVisible = true;
            to.RenderTransform = toTransform;
            to.Opacity = toOpacity;
        }
    }

    private Animation CreateAnimation(double startY, double opacity) => new()
    {
        Duration = Duration,
        Easing = EntranceEasing,
        FillMode = FillMode.None,
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.YProperty, startY), new Setter(Visual.OpacityProperty, 0d) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(TranslateTransform.YProperty, 0d), new Setter(Visual.OpacityProperty, opacity) } }
        }
    };
}
