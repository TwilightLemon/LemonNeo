using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace LemonNeo.Desktop.Controls.Lyrics;

/// <summary>Retains rasterized glyphs and halos; playback only changes compositor properties.</summary>
internal sealed class LyricWordAnimation : Control, IDisposable
{
    private const int AtlasSize = 2048;
    private readonly List<RenderTargetBitmap> _atlases = [];
    private readonly Word[] _words;
    private readonly double _fontSize;
    private readonly bool _vertical;
    private readonly TranslateTransform _origin = new();
    public double RenderScaling { get; }

    public LyricWordAnimation(LyricAnimationTiming[] timing, Rect[][] bounds, double fontSize,
        double renderScaling, bool vertical, Action<DrawingContext> drawNormal, Action<DrawingContext>? drawReached)
    {
        _fontSize = fontSize;
        _vertical = vertical;
        RenderScaling = renderScaling;
        RenderTransform = _origin;
        IsHitTestVisible = false;
        UseLayoutRounding = false;

        var cells = new List<Cell>();
        var pages = new List<(List<Cell> Cells, PixelSize Size)>();
        var x = 0;
        var y = 0;
        var rowHeight = 0;
        var atlasWidth = 1;
        var padding = Math.Ceiling(fontSize * .36 * renderScaling) / renderScaling;
        _words = new Word[timing.Length];
        for (var i = 0; i < timing.Length; i++)
        {
            var transform = new TranslateTransform();
            var pieces = new List<Piece>();
            foreach (var bound in bounds[i].SelectMany(bound => TileBounds(bound, AtlasSize / renderScaling - padding * 2)))
            {
                if (bound.Width <= 0 || bound.Height <= 0) continue;
                var width = (int)Math.Ceiling((bound.Width + padding * 2) * renderScaling);
                var height = (int)Math.Ceiling((bound.Height + padding * 2) * renderScaling);
                var destination = new Rect(bound.X - padding, bound.Y - padding,
                    width / renderScaling, height / renderScaling);
                var normal = Allocate(false, false);
                var reached = drawReached is null ? null : Allocate(true, false);
                var glow = timing[i].IsEmphasized && drawReached is not null ? Allocate(true, true) : null;
                pieces.Add(new Piece(bound, destination, normal, reached, glow));

                CachedGlyph Allocate(bool highlighted, bool blurred)
                {
                    if (x > 0 && x + width > AtlasSize)
                    {
                        x = 0;
                        y += rowHeight;
                        rowHeight = 0;
                    }
                    if (y + height > AtlasSize)
                    {
                        pages.Add((cells, new PixelSize(atlasWidth, y)));
                        cells = [];
                        x = y = rowHeight = 0;
                        atlasWidth = 1;
                    }
                    var source = new Rect(x, y, width, height);
                    var glyph = new CachedGlyph(source, destination)
                        { RenderTransform = transform, UseLayoutRounding = false, IsHitTestVisible = false };
                    cells.Add(new Cell(glyph, bound, highlighted, blurred));
                    x += width;
                    rowHeight = Math.Max(rowHeight, height);
                    atlasWidth = Math.Max(atlasWidth, x);
                    return glyph;
                }
            }
            _words[i] = new Word(timing[i], transform, pieces.ToArray(),
                pieces.Sum(piece => vertical ? piece.Bounds.Height : piece.Bounds.Width));
        }

        pages.Add((cells, new PixelSize(atlasWidth, Math.Max(1, y + rowHeight))));
        try
        {
            var blur = new BlurEffect { Radius = fontSize * .12 };
            foreach (var page in pages)
            {
                // Atlas coordinates are pixels. Keep its public image size in the same units for
                // both immediate and compositor image drawing; supersample via the drawing transform.
                var atlas = new RenderTargetBitmap(page.Size);
                _atlases.Add(atlas);
                using var context = atlas.CreateDrawingContext();
                using var scale = context.PushTransform(Matrix.CreateScale(renderScaling, renderScaling));
                using var textOptions = context.PushTextOptions(new TextOptions { TextRenderingMode = TextRenderingMode.Antialias });
                foreach (var cell in page.Cells)
                {
                    var glyph = cell.Glyph;
                    var tile = new Rect(glyph.Source.X / renderScaling, glyph.Source.Y / renderScaling,
                        glyph.Source.Width / renderScaling, glyph.Source.Height / renderScaling);
                    using (context.PushClip(tile))
                    using (context.PushTransform(Matrix.CreateTranslation(tile.X - glyph.Destination.X,
                               tile.Y - glyph.Destination.Y)))
                    {
                        if (cell.Blurred)
                        {
                            // Blur once, with transparent gutters so neighboring glyphs never bleed into the halo.
                            using (context.PushEffect(blur, cell.Bounds))
                            using (context.PushClip(cell.Bounds))
                                drawReached!(context);
                        }
                        else
                        {
                            using (context.PushClip(cell.Bounds))
                                (cell.Highlighted ? drawReached! : drawNormal)(context);
                        }
                    }
                    glyph.Bitmap = atlas;
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }

        // Keep every halo behind the sharp text, including adjacent characters.
        foreach (var cell in pages.SelectMany(page => page.Cells).Where(cell => cell.Blurred)) VisualChildren.Add(cell.Glyph);
        foreach (var cell in pages.SelectMany(page => page.Cells).Where(cell => !cell.Blurred)) VisualChildren.Add(cell.Glyph);
    }

    private static IEnumerable<Rect> TileBounds(Rect bounds, double size)
    {
        // Long unwrapped words must not exceed the backend's texture size limit.
        for (var y = bounds.Y; y < bounds.Bottom; y += size)
        for (var x = bounds.X; x < bounds.Right; x += size)
            yield return new Rect(x, y, Math.Min(size, bounds.Right - x), Math.Min(size, bounds.Bottom - y));
    }

    public void SetOrigin(Point origin)
    {
        _origin.X = origin.X;
        _origin.Y = origin.Y;
    }

    public void UpdatePosition(double position)
    {
        foreach (var word in _words)
        {
            word.Transform.Y = -_fontSize * (word.Timing.IsEmphasized ? .13 : .085) * word.Timing.LiftProgress(position);
            var progress = word.Timing.HighlightProgress(position);
            var glow = word.Timing.GlowProgress(position) * .75;
            var consumed = 0d;
            foreach (var piece in word.Pieces)
            {
                if (piece.Glow is { } halo) halo.Opacity = glow;
                var extent = _vertical ? piece.Bounds.Height : piece.Bounds.Width;
                var local = Math.Clamp((word.Extent * progress - consumed) / extent, 0, 1);
                consumed += extent;
                if (piece.Reached is not { } reached) continue;
                reached.Opacity = local > 0 ? 1 : 0;
                if (local is <= 0 or >= 1)
                {
                    reached.OpacityMask = null;
                    continue;
                }

                var edge = local * 1.3 - .15;
                var start = _vertical ? piece.Bounds.Y - piece.Destination.Y : piece.Bounds.X - piece.Destination.X;
                var size = _vertical ? piece.Destination.Height : piece.Destination.Width;
                // Only the currently sweeping fragment needs a new mask; its cached glyph never redraws.
                reached.OpacityMask = new LinearGradientBrush
                {
                    StartPoint = RelativePoint.TopLeft,
                    EndPoint = _vertical ? new RelativePoint(0, 1, RelativeUnit.Relative)
                        : new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops =
                    [
                        new GradientStop(Colors.White, (start + Math.Clamp(edge - .15, 0, 1) * extent) / size),
                        new GradientStop(Colors.Transparent, (start + Math.Clamp(edge + .15, 0, 1) * extent) / size)
                    ]
                };
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in VisualChildren.OfType<CachedGlyph>()) child.Measure(child.Destination.Size);
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in VisualChildren.OfType<CachedGlyph>()) child.Arrange(child.Destination);
        return finalSize;
    }

    public void Dispose()
    {
        VisualChildren.Clear();
        foreach (var atlas in _atlases) atlas.Dispose();
        _atlases.Clear();
    }

    private sealed record Word(LyricAnimationTiming Timing, TranslateTransform Transform, Piece[] Pieces, double Extent);
    private sealed record Piece(Rect Bounds, Rect Destination, CachedGlyph Normal, CachedGlyph? Reached, CachedGlyph? Glow);
    private sealed record Cell(CachedGlyph Glyph, Rect Bounds, bool Highlighted, bool Blurred);

    private sealed class CachedGlyph(Rect source, Rect destination) : Control
    {
        public Rect Source { get; } = source;
        public Rect Destination { get; } = destination;
        public RenderTargetBitmap? Bitmap { get; set; }

        public override void Render(DrawingContext context)
        {
            // Interpolating cached pixels also avoids Skia's quarter-pixel glyph-position quantization.
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.LowQuality }))
                context.DrawImage(Bitmap!, Source, new Rect(Bounds.Size));
        }

        protected override Size MeasureOverride(Size availableSize) => Destination.Size;
    }
}
