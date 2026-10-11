using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace LemonNeo.Desktop.Controls.Shared;

/// <summary>Blurs the already rendered page beneath a tinted, rounded surface.</summary>
public sealed class FrostedGlass : TemplatedControl
{
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.Custom(new BackdropOperation(bounds, CornerRadius));
        context.DrawRectangle(Background, new Pen(BorderBrush, BorderThickness.Left),
            new RoundedRect(bounds, CornerRadius));
    }

    private sealed class BackdropOperation(Rect bounds, CornerRadius corners) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public bool HitTest(Point point)
        {
            return false;
        }

        public bool Equals(ICustomDrawOperation? other)
        {
            return false;
        }

        public void Dispose()
        {
        }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            if (lease.SkSurface is not { } surface || bounds.Width <= 0 || bounds.Height <= 0) return;

            var canvas = lease.SkCanvas;
            var matrix = canvas.TotalMatrix;
            var rect = new SKRect(0, 0, (float)bounds.Width, (float)bounds.Height);
            var deviceRect = matrix.MapRect(rect);
            var scaleX = MathF.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY);
            var scaleY = MathF.Sqrt(matrix.ScaleY * matrix.ScaleY + matrix.SkewX * matrix.SkewX);
            var sigmaX = 12 * scaleX;
            var sigmaY = 12 * scaleY;
            // Sample beyond the card edges so the blur has no dark or transparent fringe.
            deviceRect.Inflate(3 * sigmaX, 3 * sigmaY);
            var capture = new SKRectI(
                Math.Max(0, (int)MathF.Floor(deviceRect.Left)),
                Math.Max(0, (int)MathF.Floor(deviceRect.Top)),
                Math.Min(surface.Canvas.DeviceClipBounds.Right, (int)MathF.Ceiling(deviceRect.Right)),
                Math.Min(surface.Canvas.DeviceClipBounds.Bottom, (int)MathF.Ceiling(deviceRect.Bottom)));
            if (capture.Width <= 0 || capture.Height <= 0) return;

            using var image = surface.Snapshot(capture);
            using var blur = SKImageFilter.CreateBlur(sigmaX, sigmaY, SKShaderTileMode.Clamp);
            using var paint = new SKPaint
            {
                ImageFilter = blur,
                Color = SKColors.White.WithAlpha((byte)Math.Clamp(lease.CurrentOpacity * 255, 0, 255))
            };
            using var clip = new SKRoundRect();
            clip.SetRectRadii(rect,
            [
                new SKPoint((float)corners.TopLeft, (float)corners.TopLeft),
                new SKPoint((float)corners.TopRight, (float)corners.TopRight),
                new SKPoint((float)corners.BottomRight, (float)corners.BottomRight),
                new SKPoint((float)corners.BottomLeft, (float)corners.BottomLeft)
            ]);
            canvas.Save();
            try
            {
                canvas.ClipRoundRect(clip, SKClipOperation.Intersect, true);
                canvas.ResetMatrix();
                canvas.DrawImage(image, capture.Left, capture.Top, paint);
            }
            finally
            {
                canvas.Restore();
            }
        }
    }
}