using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace LemonNeo.Desktop.Controls.Shared;

/// <summary>
/// Draws text the way Apple Music lights up a highlighted label: the backdrop behind the control
/// is sampled, the highlight colour is added to it, and the text shape is used to cut that result
/// back out. The glyphs are re-emitted as vector glyph runs instead of a re-rasterized bitmap, so
/// the added colour never softens their edges.
/// </summary>
public sealed class AdditiveTextBlock : TextBlock
{
    public static readonly StyledProperty<bool> IsHighlightEnabledProperty =
        AvaloniaProperty.Register<AdditiveTextBlock, bool>(nameof(IsHighlightEnabled));

    /// <summary>Adds the highlight to the backdrop; disabling it mixes the two colours instead.</summary>
    public static readonly StyledProperty<bool> UseAdditiveProperty =
        AvaloniaProperty.Register<AdditiveTextBlock, bool>(nameof(UseAdditive), true);

    /// <summary>Contributes nothing until a caller opts in, so enabling the highlight alone keeps
    /// the foreground colour's own brightness.</summary>
    public static readonly StyledProperty<Color> HighlightColorProperty =
        AvaloniaProperty.Register<AdditiveTextBlock, Color>(nameof(HighlightColor), Colors.Transparent);

    public static readonly StyledProperty<double> HighlightIntensityProperty =
        AvaloniaProperty.Register<AdditiveTextBlock, double>(nameof(HighlightIntensity), 1,
            coerce: (_, value) => double.IsFinite(value) ? Math.Max(0, value) : 1);

    // The glyph list outlives a single frame, so it is rebuilt only when the layout is replaced.
    private readonly List<GlyphDraw> _glyphs = [];
    private TextLayout? _glyphLayout;
    private Point _glyphOrigin;

    static AdditiveTextBlock()
    {
        AffectsRender<AdditiveTextBlock>(IsHighlightEnabledProperty, UseAdditiveProperty,
            HighlightColorProperty, HighlightIntensityProperty);
    }

    public bool IsHighlightEnabled
    {
        get => GetValue(IsHighlightEnabledProperty);
        set => SetValue(IsHighlightEnabledProperty, value);
    }

    public bool UseAdditive
    {
        get => GetValue(UseAdditiveProperty);
        set => SetValue(UseAdditiveProperty, value);
    }

    public Color HighlightColor
    {
        get => GetValue(HighlightColorProperty);
        set => SetValue(HighlightColorProperty, value);
    }

    public double HighlightIntensity
    {
        get => GetValue(HighlightIntensityProperty);
        set => SetValue(HighlightIntensityProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        if (!IsHighlightEnabled || Foreground is not ISolidColorBrush foreground ||
            Bounds.Width <= 0 || Bounds.Height <= 0 || !EnsureGlyphs(origin))
        {
            base.RenderTextLayout(context, origin);
            return;
        }

        context.Custom(new HighlightOperation(new Rect(Bounds.Size), new List<GlyphDraw>(_glyphs),
            foreground.Color, foreground.Opacity, HighlightColor, HighlightIntensity, UseAdditive));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ClearGlyphs();
        base.OnDetachedFromVisualTree(e);
    }

    private void ClearGlyphs()
    {
        _glyphLayout = null;
        _glyphs.Clear();
    }

    /// <summary>
    /// Replays the text layout into a drawing group only to read its glyph runs back. Anything the
    /// replay wraps in a clip, mask or effect cannot be lifted out that way, and the caller then
    /// falls back to normal text rendering.
    /// </summary>
    private bool EnsureGlyphs(Point origin)
    {
        var layout = TextLayout;
        if (ReferenceEquals(layout, _glyphLayout) && _glyphOrigin == origin) return _glyphs.Count > 0;
        ClearGlyphs();
        _glyphLayout = layout;
        _glyphOrigin = origin;
        var recording = new DrawingGroup();
        using (var target = recording.Open()) layout.Draw(target, origin);
        return Collect(recording, Matrix.Identity, _glyphs);
    }

    private static bool Collect(Drawing drawing, Matrix transform, List<GlyphDraw> glyphs)
    {
        switch (drawing)
        {
            case GlyphRunDrawing { GlyphRun: { } run }:
                glyphs.Add(new GlyphDraw(run, transform));
                return true;
            case DrawingGroup group
                when group.ClipGeometry is null && group.OpacityMask is null && group.Effect is null &&
                     group.Opacity == 1:
            {
                var nested = (group.Transform?.Value ?? Matrix.Identity) * transform;
                return group.Children.All(child => Collect(child, nested, glyphs));
            }
            default:
                return false;
        }
    }

    private sealed record GlyphDraw(GlyphRun Run, Matrix Transform);

    private sealed class HighlightOperation(
        Rect bounds,
        List<GlyphDraw> glyphs,
        Color foreground,
        double foregroundOpacity,
        Color highlight,
        double intensity,
        bool useAdditive) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public bool HitTest(Point point) => false;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose()
        {
        }

        public void Render(ImmediateDrawingContext context)
        {
            var backdrop = SKColors.Transparent;
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is { } feature)
            {
                using var lease = feature.Lease();
                if (lease.SkSurface is { } surface)
                    backdrop = Sample(surface, lease.SkCanvas, bounds);
            }

            var brush = new ImmutableSolidColorBrush(Compose(backdrop));
            foreach (var glyph in glyphs)
            {
                var reference = glyph.Run.TryCreateImmutableGlyphRunReference();
                if (reference is null) continue;
                using (reference)
                {
                    if (glyph.Transform.IsIdentity)
                    {
                        context.DrawGlyphRun(brush, reference);
                        continue;
                    }

                    using (context.PushPreTransform(glyph.Transform)) context.DrawGlyphRun(brush, reference);
                }
            }
        }

        /// <summary>Average colour of the already painted page beneath the text. A page rendered
        /// into its own layer reports nothing, which leaves the foreground colour untouched.</summary>
        private static SKColor Sample(SKSurface surface, SKCanvas canvas, Rect area)
        {
            var matrix = canvas.TotalMatrix;
            var device = matrix.MapRect(new SKRect(0, 0, (float)area.Width, (float)area.Height));
            var clip = canvas.DeviceClipBounds;
            var capture = new SKRectI(
                Math.Max(clip.Left, (int)MathF.Floor(device.Left)),
                Math.Max(clip.Top, (int)MathF.Floor(device.Top)),
                Math.Min(clip.Right, (int)MathF.Ceiling(device.Right)),
                Math.Min(clip.Bottom, (int)MathF.Ceiling(device.Bottom)));
            if (capture.Width <= 0 || capture.Height <= 0) return SKColors.Transparent;

            using var image = surface.Snapshot(capture);
            var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var pixels = new SKBitmap(info);
            if (!image.ReadPixels(info, pixels.GetPixels(), info.RowBytes, 0, 0)) return SKColors.Transparent;

            // The backdrop is a smooth gradient, so a sparse grid describes it as well as every pixel
            // would and keeps the per-frame cost flat.
            var step = Math.Max(1, Math.Min(image.Width, image.Height) / 8);
            double red = 0, green = 0, blue = 0;
            var samples = 0;
            for (var y = 0; y < image.Height; y += step)
            for (var x = 0; x < image.Width; x += step)
            {
                var sample = pixels.GetPixel(x, y);
                red += sample.Red;
                green += sample.Green;
                blue += sample.Blue;
                samples++;
            }

            if (samples == 0) return SKColors.Transparent;
            return new SKColor(Channel(red / samples), Channel(green / samples), Channel(blue / samples));
        }

        private Color Compose(SKColor backdrop)
        {
            var coverage = foreground.A / 255d * foregroundOpacity;
            var strength = Math.Clamp(highlight.A / 255d * intensity, 0, 1);
            if (!useAdditive)
                return Color.FromArgb(
                    Channel((coverage + (1 - coverage) * strength) * 255),
                    Channel(foreground.R + (highlight.R - foreground.R) * strength),
                    Channel(foreground.G + (highlight.G - foreground.G) * strength),
                    Channel(foreground.B + (highlight.B - foreground.B) * strength));

            var red = backdrop.Red + foreground.R * coverage + highlight.R * strength;
            var green = backdrop.Green + foreground.G * coverage + highlight.G * strength;
            var blue = backdrop.Blue + foreground.B * coverage + highlight.B * strength;
            // The highlight adds light rather than replacing the glyph's own coverage, so both
            // contributions survive into the alpha the glyph is finally drawn with.
            return Color.FromArgb(Channel((coverage + strength) * 255),
                Channel(red), Channel(green), Channel(blue));
        }

        private static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
    }
}
