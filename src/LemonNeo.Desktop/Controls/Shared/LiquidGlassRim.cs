using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace LemonNeo.Desktop.Controls.Shared;

/// <summary>Refracts the scene at the rim of a glass surface, underneath its content.</summary>
public sealed class LiquidGlassRim : TemplatedControl
{
    private const float RimWidth = 16;
    private const float SoftwareRimPixels = 12;

    static LiquidGlassRim() => AffectsRender<LiquidGlassRim>(CornerRadiusProperty);

    public override void Render(DrawingContext context)
    {
        if (Bounds is { Width: > 0, Height: > 0 })
            context.Custom(new RimOperation(new Rect(Bounds.Size), CornerRadius));
    }

    private sealed class RimOperation(Rect bounds, CornerRadius corners) : ICustomDrawOperation
    {
        private RimMaterial? _material;
        private float _configuredWidth;

        public Rect Bounds => bounds;
        public bool HitTest(Point point) => false;
        public bool Equals(ICustomDrawOperation? other) =>
            other is RimOperation rim && bounds == rim.Bounds && corners == rim.Corners;
        private CornerRadius Corners => corners;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            var width = RimWidth;
            if (lease.GrContext is null)
            {
                // Bound the software shader's strip width in device pixels on high-DPI displays.
                var m = lease.SkCanvas.TotalMatrix;
                var scaleSquared = Math.Max(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY,
                    m.ScaleY * m.ScaleY + m.SkewX * m.SkewX);
                width = Math.Min(width, SoftwareRimPixels / MathF.Sqrt(Math.Max(1, scaleSquared)));
            }
            if (_material is null || Math.Abs(_configuredWidth - width) > .05f)
            {
                _material?.Dispose();
                _material = new RimMaterial(bounds, corners, width);
                _configuredWidth = width;
            }
            _material.Draw(lease.SkCanvas, lease.SkSurface, lease.CurrentOpacity);
        }

        public void Dispose() => _material?.Dispose();
    }

    private sealed class RimMaterial : IDisposable
    {
        // Signed distance and outward normal of the same rounded rectangle that clips the blur.
        private static readonly SKRuntimeEffect Lens = CreateLens();
        private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);
        private readonly SKRuntimeEffectUniforms _uniforms;
        private readonly SKRuntimeEffectChildren _inputs = new(Lens);
        private readonly SKRoundRect _outer = new();
        private readonly SKRoundRect _inner;
        private readonly SKPaint _paint = new();
        private readonly SKRect[] _bands;
        private readonly float _samplingHalo;

        public RimMaterial(Rect bounds, CornerRadius corners, float requestedWidth)
        {
            var w = (float)bounds.Width;
            var h = (float)bounds.Height;
            var limit = Math.Min(w, h) * .5f;
            var width = Math.Min(requestedWidth, limit);
            _samplingHalo = 1 + 2.2f * width; // Reach of the furthest wavelength plus bilinear sampling.
            float Radius(double r) => Math.Clamp((float)r, 0, limit);
            float[] radii = [Radius(corners.TopLeft), Radius(corners.TopRight),
                Radius(corners.BottomRight), Radius(corners.BottomLeft)];
            _outer.SetRectRadii(SKRect.Create(w, h), Array.ConvertAll(radii, r => new SKPoint(r, r)));
            _inner = new SKRoundRect(_outer);
            _inner.Deflate(width, width);

            // Partition at the ends of the curved corners. The side strips then sample horizontally.
            var cap = Math.Max(width, radii.Max());
            _bands = [new(0, 0, w, cap), new(0, h - cap, w, h),
                new(0, cap, width, h - cap), new(w - width, cap, w, h - cap)];

            _uniforms = new SKRuntimeEffectUniforms(Lens)
            {
                ["halfSize"] = new SKPoint(w / 2, h / 2), ["radii"] = radii, ["rimWidth"] = width
            };
        }

        public void Draw(SKCanvas canvas, SKSurface? surface, double opacity)
        {
            var transform = canvas.TotalMatrix;
            if (!transform.TryInvert(out var deviceToLocal) || opacity <= 0) return;
            _paint.Color = SKColors.White.WithAlpha((byte)Math.Clamp(opacity * 255, 0, 255));
            var dirty = canvas.LocalClipBounds;
            var saved = canvas.Save();
            try
            {
                canvas.ClipRoundRect(_outer, SKClipOperation.Intersect, true);
                if (!_inner.Rect.IsEmpty) canvas.ClipRoundRect(_inner, SKClipOperation.Difference, true);
                foreach (var band in _bands)
                {
                    var output = SKRect.Intersect(band, dirty);
                    if (output.IsEmpty || canvas.QuickReject(output)) continue;

                    // Snapshot only this visible strip plus its sampling halo. Skia keeps GPU surfaces
                    // on the GPU. The shader matrix anchors texture coordinates to the scene at any DPI.
                    var sample = output;
                    sample.Inflate(_samplingHalo, _samplingHalo);
                    sample.Intersect(_outer.Rect);
                    var device = transform.MapRect(sample);
                    var region = new SKRectI(Math.Max(0, (int)MathF.Floor(device.Left)),
                        Math.Max(0, (int)MathF.Floor(device.Top)),
                        (int)MathF.Ceiling(device.Right), (int)MathF.Ceiling(device.Bottom));
                    if (region.IsEmpty) continue;
                    using var image = surface?.Snapshot(region);
                    var local = deviceToLocal.PreConcat(SKMatrix.CreateTranslation(region.Left, region.Top));
                    using var scene = image?.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Sampling, local)
                                      ?? SKShader.CreateColor(SKColors.Transparent);
                    _inputs["scene"] = scene;
                    using var lens = Lens.ToShader(_uniforms, _inputs);
                    _paint.Shader = lens;
                    canvas.DrawRect(output, _paint);
                }
            }
            finally
            {
                _paint.Shader = null;
                _inputs["scene"] = null;
                canvas.RestoreToCount(saved);
            }
        }

        public void Dispose()
        {
            _outer.Dispose();
            _inner.Dispose();
            _uniforms.Dispose();
            _inputs.Dispose();
            _paint.Dispose();
        }

        private static SKRuntimeEffect CreateLens() => SKRuntimeEffect.CreateShader("""
            uniform shader scene;
            uniform float2 halfSize;
            uniform float4 radii;
            uniform float rimWidth;

            half4 main(float2 p) {
                float2 v = p - halfSize;
                float r = v.y < 0 ? (v.x < 0 ? radii.x : radii.y) : (v.x < 0 ? radii.w : radii.z);
                float2 q = abs(v) - halfSize + r;
                float2 arc = max(q, 0);
                float len = length(arc);
                float2 n = len > 0.0001 ? arc / len : (q.x > q.y ? float2(1, 0) : float2(0, 1));
                n *= sign(v);
                float depth = r - len - min(max(q.x, q.y), 0);
                float t = clamp(depth / rimWidth, 0, 1);
                float shoulder = 1 - t;
                float curve = shoulder * shoulder * (0.62 + 0.38 * shoulder);
                float2 relative = v / halfSize;
                float corner = abs(relative.x * relative.y);

                // Bend towards the glass interior so all wavelengths sample the same frosted scene.
                float2 bent = p - n * (rimWidth * curve * (1.72 + 0.24 * corner));
                float2 spread = n * (rimWidth * curve * (0.025 + 0.20 * corner));
                half4 centre = scene.eval(bent);
                half4 red = scene.eval(bent - spread);
                half4 blue = scene.eval(bent + spread);
                half3 spectrum = half3(red.r, centre.g, blue.b);
                half4 refracted = half4(mix(centre.rgb, spectrum, 0.78),
                                         max(centre.a, max(red.a, blue.a)));

                float facing = dot(n, float2(-0.48, -0.877));
                float crest = 1 - smoothstep(0, min(1.35, 0.18 * rimWidth), depth);
                float glint = max(facing, 0);
                float light = crest * (0.035 + 0.21 * glint * glint + 0.095 * max(-facing, 0))
                            + 0.025 * curve * glint;
                float shade = 0.075 * curve * max(-facing, 0);
                half4 reflection = half4(half3(light), light + shade * (1 - light));
                float fade = 0.92 * (1 - smoothstep(0.08, 1, t));
                return reflection + (1 - reflection.a) * fade * refracted;
            }
            """, out var error) ?? throw new InvalidOperationException(error);
    }
}
