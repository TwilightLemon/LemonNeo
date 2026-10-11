using System.Diagnostics;
using System.Numerics;
using SkiaSharp;

namespace LemonNeo.Desktop.Controls.Backgrounds;

/// <summary>
/// Skia port of the modern Lyricify Apple Music Inspired · iOS render passes.
/// The mesh data is compiled from the vendored Apache-2.0 reference source.
/// </summary>
internal sealed class AppleMusicMeshRenderer : IDisposable
{
    private const double ArtworkTransitionSeconds = .5;
    private const float LyricsBlurSigma = 42.5f;
    private const float OrdinaryBlurSigma = 80f;
    private const float ImagePulseIntensity = .33f;
    private const int MaximumArtworkDimension = 768;
    private const int LuminanceHistogramSize = 64;
    private const float DarkThemeMaximumAverageLuminance = .36f;
    private const float DarkThemeMaximumHighlightLuminance = .64f;
    private const float LightThemeMinimumAverageLuminance = .54f;
    private const float LightThemeMinimumShadowLuminance = .24f;
    private const float ThemeAdjustmentContrast = .9f;

    private static readonly SKSamplingOptions Sampling =
        new(SKCubicResampler.Mitchell);

    private static readonly Vector2[] QuadCorners =
    [
        new(-1f, 1f), new(1f, 1f), new(1f, -1f), new(-1f, -1f)
    ];

    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int _randomPresetSlot = Random.Shared.Next(AppleMusicIosMesh.PresetSlotCount);
    private readonly SKPoint[][] _rotatingQuads =
    [
        new SKPoint[4], new SKPoint[4], new SKPoint[4]
    ];
    private MeshData? _landscapeMesh;
    private int _landscapePreset = -1;
    private MeshData? _portraitMesh;
    private int _portraitPreset = -1;
    private DecodedArtwork? _currentArtwork;
    private string _currentArtworkId = string.Empty;
    private bool _disposed;
    private DecodedArtwork? _previousArtwork;
    private volatile bool _artworkTransitionActive;
    private double _transitionStart;

    public bool HasArtwork
    {
        get
        {
            lock (_gate) return !_disposed && _currentArtwork is not null;
        }
    }

    public bool IsArtworkTransitionActive => _artworkTransitionActive;

    internal static DecodedArtwork DecodeArtwork(BackgroundArtworkFrame artwork)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        if (artwork.EncodedData.IsEmpty) throw new InvalidDataException("Artwork data is empty.");

        using var bitmap = SKBitmap.Decode(artwork.EncodedData.Span)
            ?? throw new InvalidDataException("Artwork could not be decoded.");
        var maximumDimension = Math.Max(bitmap.Width, bitmap.Height);
        if (maximumDimension <= MaximumArtworkDimension)
            return CreateDecodedArtwork(bitmap);

        var scale = MaximumArtworkDimension / (float)maximumDimension;
        var size = new SKSizeI(
            Math.Max(1, (int)MathF.Round(bitmap.Width * scale)),
            Math.Max(1, (int)MathF.Round(bitmap.Height * scale)));
        using var resized = bitmap.Resize(size, Sampling)
            ?? throw new InvalidDataException("Artwork could not be resized.");
        return CreateDecodedArtwork(resized);
    }

    internal void SetArtwork(DecodedArtwork artwork, string id)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        lock (_gate)
        {
            if (_disposed)
            {
                artwork.Dispose();
                return;
            }

            if (string.Equals(_currentArtworkId, id, StringComparison.Ordinal) && _currentArtwork is not null)
            {
                artwork.Dispose();
                return;
            }

            _previousArtwork?.Dispose();
            _previousArtwork = _currentArtwork;
            _currentArtwork = artwork;
            _currentArtworkId = id ?? string.Empty;
            _transitionStart = _clock.Elapsed.TotalSeconds;
            _artworkTransitionActive = _previousArtwork is not null;
        }
    }

    public void ClearArtwork()
    {
        lock (_gate)
        {
            _currentArtwork?.Dispose();
            _previousArtwork?.Dispose();
            _currentArtwork = null;
            _previousArtwork = null;
            _currentArtworkId = string.Empty;
            _artworkTransitionActive = false;
        }
    }

    public void Render(SKCanvas canvas, GRContext? gpuContext, RenderFrame frame)
    {
        lock (_gate)
        {
            // Never start a private CPU rendering pipeline. If Avalonia has no active
            // GPU context, its renderer remains solely responsible for backend fallback.
            if (_disposed || _currentArtwork is null || frame.Width <= 0 || frame.Height <= 0 ||
                gpuContext is null || gpuContext.IsAbandoned) return;

            var renderScale = (float)Math.Clamp(frame.RenderScale, .125d, 1d);
            var pixelWidth = Math.Max(1, (int)MathF.Ceiling(frame.Width * frame.DeviceScaleX * renderScale));
            var pixelHeight = Math.Max(1, (int)MathF.Ceiling(frame.Height * frame.DeviceScaleY * renderScale));
            var info = new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul);

            // These surfaces are GPU-backed and scoped to the active Skia lease. Keeping
            // their lifetime on the composition thread avoids cross-thread GRContext use;
            // Skia's resource cache reuses the underlying allocations between frames.
            using var rotationSurface = SKSurface.Create(gpuContext, true, info);
            using var blurSurface = SKSurface.Create(gpuContext, true, info);
            if (rotationSurface is null || blurSurface is null) return;

            var clockTime = (float)_clock.Elapsed.TotalSeconds;
            var time = frame.AnimationTime;
            var transitionMix = frame.IsArtworkTransitionEnabled
                ? GetTransitionMix(clockTime)
                : CompleteTransition();
            var processedPulse = SmootherStep(Math.Clamp(frame.LowFrequencyPulse, 0f, 1f));
            var imageScale = 1f + ImagePulseIntensity * processedPulse * processedPulse *
                (float)Math.Clamp(frame.BassPulseScale, 0d, 10d);
            UpdateRotatingQuads(pixelWidth, pixelHeight, time,
                (float)Math.Clamp(frame.RotationScale, 0d, 10d), imageScale);

            RenderRotationPass(rotationSurface.Canvas, pixelWidth, pixelHeight, transitionMix,
                frame.IsDarkTheme);

            using var rotationImage = rotationSurface.Snapshot();
            var blurSigma = (frame.IsMeshEnabled ? LyricsBlurSigma : OrdinaryBlurSigma) * renderScale *
                (float)Math.Clamp(frame.BlurScale, 0d, 10d);
            RenderBlurPass(blurSurface.Canvas, rotationImage, pixelWidth, pixelHeight, blurSigma);

            using var blurredImage = blurSurface.Snapshot();
            using var outputPaint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.White.WithAlpha((byte)Math.Clamp(frame.Opacity * 255d, 0d, 255d))
            };
            if (frame.IsMeshEnabled)
            {
                DrawPinchMesh(canvas, blurredImage, frame, time, outputPaint);
            }
            else
            {
                canvas.DrawImage(blurredImage, new SKRect(0, 0, pixelWidth, pixelHeight),
                    new SKRect(0, 0, frame.Width, frame.Height), Sampling, outputPaint);
            }
        }
    }

    private void RenderRotationPass(SKCanvas canvas, int width, int height, float transitionMix,
        bool isDarkTheme)
    {
        canvas.Clear(SKColors.Black);
        if (_previousArtwork is not null && transitionMix < 1f)
            DrawArtworkLayers(canvas, _previousArtwork, width, height, 1f - transitionMix,
                isDarkTheme);
        if (_currentArtwork is not null)
            DrawArtworkLayers(canvas, _currentArtwork, width, height, transitionMix, isDarkTheme);
    }

    private void DrawArtworkLayers(SKCanvas canvas, DecodedArtwork artwork, int width, int height,
        float opacity, bool isDarkTheme)
    {
        if (opacity <= 0f) return;
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.White.WithAlpha((byte)Math.Clamp(opacity * 255f, 0f, 255f)),
            ColorFilter = isDarkTheme ? artwork.DarkThemeFilter : artwork.LightThemeFilter
        };
        var image = artwork.Image;
        var source = GetAspectFillSource(image.Width, image.Height, width, height);
        canvas.DrawImage(image, source, new SKRect(0, 0, width, height), Sampling, paint);

        using var shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Sampling);
        paint.Shader = shader;
        for (var instance = 0; instance < 3; instance++)
        {
            using var vertices = SKVertices.CreateCopy(
                SKVertexMode.TriangleFan, _rotatingQuads[instance], artwork.TextureCoordinates, null);
            canvas.DrawVertices(vertices, SKBlendMode.Modulate, paint);
        }
    }

    private void UpdateRotatingQuads(int width, int height, float time,
        float rotationScale, float imageScale)
    {
        var aspect = width / (float)height;
        var viewScale = aspect >= 1f ? new Vector2(1f, aspect) : new Vector2(1f / aspect, 1f);
        var parentAngle = time * rotationScale * MathF.Tau / 120f;
        var parentSine = MathF.Sin(parentAngle);
        var parentCosine = MathF.Cos(parentAngle);
        for (var instance = 0; instance < _rotatingQuads.Length; instance++)
        {
            var modelScale = instance == 0 ? 1.4f : .7f;
            var translation = instance switch
            {
                1 => new Vector2(-.25f, .15f),
                2 => new Vector2(.7f, .7f),
                _ => Vector2.Zero
            };
            var period = instance switch
            {
                1 => 70f,
                2 => 90f,
                _ => 120f
            };
            var angle = time * rotationScale * MathF.Tau / period;
            var sine = MathF.Sin(angle);
            var cosine = MathF.Cos(angle);
            var result = _rotatingQuads[instance];
            for (var index = 0; index < QuadCorners.Length; index++)
            {
                var position = Rotate(QuadCorners[index], sine, cosine) * modelScale + translation;
                position *= viewScale;
                if (instance == 2) position = Rotate(position, parentSine, parentCosine);
                position *= imageScale;
                result[index] = new SKPoint(
                    (position.X * .5f + .5f) * width,
                    (.5f - position.Y * .5f) * height);
            }
        }
    }

    private static void RenderBlurPass(SKCanvas canvas, SKImage source, int width, int height, float sigma)
    {
        canvas.Clear(SKColors.Black);
        using var blur = SKImageFilter.CreateBlur(MathF.Max(0f, sigma), MathF.Max(0f, sigma),
            SKShaderTileMode.Clamp);
        using var paint = new SKPaint { ImageFilter = blur };
        canvas.DrawImage(source, new SKRect(0, 0, width, height), Sampling, paint);
    }

    private void DrawPinchMesh(SKCanvas canvas, SKImage material, RenderFrame frame, float time, SKPaint paint)
    {
        var vertical = frame.Height > frame.Width;
        var slot = frame.PresetSlot is >= 0 and < AppleMusicIosMesh.PresetSlotCount
            ? frame.PresetSlot
            : _randomPresetSlot;
        var mesh = GetMesh(vertical, slot);
        var phase = MathF.Acos(MathF.Sin(time * MathF.PI / 5f)) / MathF.PI;
        var mix = phase * phase * (3f - 2f * phase);
        var textureScale = vertical ? 1f : .8f;
        var textureOffset = (1f - textureScale) * .5f;
        for (var index = 0; index < mesh.Vertices.Length; index++)
        {
            var vertex = mesh.Vertices[index];
            var position = Vector2.Lerp(vertex.From, vertex.To, mix);
            mesh.Positions[index] = new SKPoint(
                (position.X * .5f + .5f) * frame.Width,
                (.5f - position.Y * .5f) * frame.Height);
        }

        if (mesh.TextureWidth != material.Width || mesh.TextureHeight != material.Height)
        {
            for (var index = 0; index < mesh.Vertices.Length; index++)
            {
                var coordinate = mesh.Vertices[index].TextureCoordinate;
                mesh.TextureCoordinates[index] = new SKPoint(
                    (coordinate.X * textureScale + textureOffset) * material.Width,
                    (coordinate.Y * textureScale + textureOffset) * material.Height);
            }

            mesh.TextureWidth = material.Width;
            mesh.TextureHeight = material.Height;
        }

        using var shader = material.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Sampling);
        var previousShader = paint.Shader;
        paint.Shader = shader;
        try
        {
            using var vertices = SKVertices.CreateCopy(
                SKVertexMode.Triangles, mesh.Positions, mesh.TextureCoordinates, null, mesh.Indices);
            canvas.DrawVertices(vertices, SKBlendMode.SrcOver, paint);
        }
        finally
        {
            paint.Shader = previousShader;
        }
    }

    private MeshData GetMesh(bool vertical, int slot)
    {
        var preset = vertical ? AppleMusicIosMesh.ResolvePortraitPreset(slot) : slot;
        if (vertical)
        {
            if (_portraitMesh is null || _portraitPreset != preset)
            {
                var value = AppleMusicIosMesh.Create(preset, true);
                _portraitMesh = new MeshData(value.Vertices, value.Indices);
                _portraitPreset = preset;
            }

            return _portraitMesh;
        }

        if (_landscapeMesh is null || _landscapePreset != preset)
        {
            var value = AppleMusicIosMesh.Create(preset, false);
            _landscapeMesh = new MeshData(value.Vertices, value.Indices);
            _landscapePreset = preset;
        }

        return _landscapeMesh;
    }

    private float GetTransitionMix(float time)
    {
        if (_previousArtwork is null)
        {
            _artworkTransitionActive = false;
            return 1f;
        }

        var progress = (float)((time - _transitionStart) / ArtworkTransitionSeconds);
        if (progress < 1f) return Math.Clamp(progress, 0f, 1f);
        _previousArtwork.Dispose();
        _previousArtwork = null;
        _artworkTransitionActive = false;
        return 1f;
    }

    private float CompleteTransition()
    {
        _previousArtwork?.Dispose();
        _previousArtwork = null;
        _artworkTransitionActive = false;
        return 1f;
    }

    private static DecodedArtwork CreateDecodedArtwork(SKBitmap bitmap)
    {
        var statistics = AnalyzeLuminance(bitmap);
        return new DecodedArtwork(SKImage.FromBitmap(bitmap), statistics);
    }

    private static LuminanceStatistics AnalyzeLuminance(SKBitmap bitmap)
    {
        var histogram = new double[LuminanceHistogramSize];
        var sampleStepX = Math.Max(1, bitmap.Width / 128);
        var sampleStepY = Math.Max(1, bitmap.Height / 128);
        double luminanceTotal = 0;
        double weightTotal = 0;
        for (var y = sampleStepY / 2; y < bitmap.Height; y += sampleStepY)
        {
            for (var x = sampleStepX / 2; x < bitmap.Width; x += sampleStepX)
            {
                var color = bitmap.GetPixel(x, y);
                var weight = color.Alpha / 255d;
                if (weight <= 0) continue;
                // The color matrix operates on encoded RGB values, so measure in the
                // same space instead of converting the samples to linear light.
                var luminance = (.2126d * color.Red + .7152d * color.Green + .0722d * color.Blue) / 255d;
                luminanceTotal += luminance * weight;
                weightTotal += weight;
                var bin = Math.Min(LuminanceHistogramSize - 1,
                    (int)(luminance * LuminanceHistogramSize));
                histogram[bin] += weight;
            }
        }

        if (weightTotal <= 0) return new LuminanceStatistics(.5f, .5f, .5f);
        return new LuminanceStatistics(
            (float)(luminanceTotal / weightTotal),
            FindPercentile(histogram, weightTotal, .1d),
            FindPercentile(histogram, weightTotal, .9d));
    }

    private static float FindPercentile(double[] histogram, double total, double percentile)
    {
        var target = total * percentile;
        double cumulative = 0;
        for (var index = 0; index < histogram.Length; index++)
        {
            cumulative += histogram[index];
            if (cumulative >= target) return (index + .5f) / histogram.Length;
        }

        return 1f;
    }

    private static SKColorFilter? CreateThemeFilter(LuminanceStatistics statistics, bool isDarkTheme)
    {
        float scale;
        float offset;
        if (isDarkTheme)
        {
            var averageScale = statistics.Average > 0
                ? DarkThemeMaximumAverageLuminance / statistics.Average
                : 1f;
            var highlightScale = statistics.Highlight > 0
                ? DarkThemeMaximumHighlightLuminance / statistics.Highlight
                : 1f;
            var brightnessScale = Math.Min(1f, Math.Min(averageScale, highlightScale));
            if (brightnessScale >= .999f) return null;

            var adjustedAverage = statistics.Average * brightnessScale;
            scale = brightnessScale * ThemeAdjustmentContrast;
            offset = adjustedAverage * (1f - ThemeAdjustmentContrast);
        }
        else
        {
            var averageLift = statistics.Average < LightThemeMinimumAverageLuminance
                ? (LightThemeMinimumAverageLuminance - statistics.Average) / (1f - statistics.Average)
                : 0f;
            var shadowLift = statistics.Shadow < LightThemeMinimumShadowLuminance
                ? (LightThemeMinimumShadowLuminance - statistics.Shadow) / (1f - statistics.Shadow)
                : 0f;
            var whiteBlend = Math.Clamp(Math.Max(averageLift, shadowLift), 0f, 1f);
            if (whiteBlend <= .001f) return null;

            var brightnessScale = 1f - whiteBlend;
            var adjustedAverage = statistics.Average * brightnessScale + whiteBlend;
            scale = brightnessScale * ThemeAdjustmentContrast;
            offset = whiteBlend * ThemeAdjustmentContrast +
                     adjustedAverage * (1f - ThemeAdjustmentContrast);
        }

        // SkiaSharp 3.x interprets the translation column in normalized [0, 1] units.
        return SKColorFilter.CreateColorMatrix(
        [
            scale, 0, 0, 0, offset,
            0, scale, 0, 0, offset,
            0, 0, scale, 0, offset,
            0, 0, 0, 1, 0
        ]);
    }

    private static SKRect GetAspectFillSource(int imageWidth, int imageHeight, int destinationWidth,
        int destinationHeight)
    {
        var imageAspect = imageWidth / (float)imageHeight;
        var destinationAspect = destinationWidth / (float)destinationHeight;
        if (imageAspect > destinationAspect)
        {
            var sourceWidth = imageHeight * destinationAspect;
            var left = (imageWidth - sourceWidth) * .5f;
            return new SKRect(left, 0, left + sourceWidth, imageHeight);
        }

        var sourceHeight = imageWidth / destinationAspect;
        var top = (imageHeight - sourceHeight) * .5f;
        return new SKRect(0, top, imageWidth, top + sourceHeight);
    }

    private static Vector2 Rotate(Vector2 value, float sine, float cosine)
    {
        return new Vector2(cosine * value.X - sine * value.Y,
            sine * value.X + cosine * value.Y);
    }

    private static float SmootherStep(float value)
    {
        return value * value * value * (value * (value * 6f - 15f) + 10f);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _currentArtwork?.Dispose();
            _previousArtwork?.Dispose();
            _currentArtwork = null;
            _previousArtwork = null;
            _artworkTransitionActive = false;
        }
    }

    internal readonly record struct RenderFrame(
        float Width,
        float Height,
        float DeviceScaleX,
        float DeviceScaleY,
        double Opacity,
        double RenderScale,
        double RotationScale,
        double BassPulseScale,
        double BlurScale,
        float AnimationTime,
        float LowFrequencyPulse,
        int PresetSlot,
        bool IsMeshEnabled,
        bool IsArtworkTransitionEnabled,
        bool IsDarkTheme);

    internal readonly record struct LuminanceStatistics(float Average, float Shadow, float Highlight);

    internal sealed class DecodedArtwork : IDisposable
    {
        internal DecodedArtwork(SKImage image, LuminanceStatistics statistics)
        {
            Image = image;
            TextureCoordinates =
            [
                new(0, 0), new(image.Width, 0),
                new(image.Width, image.Height), new(0, image.Height)
            ];
            DarkThemeFilter = CreateThemeFilter(statistics, true);
            LightThemeFilter = CreateThemeFilter(statistics, false);
        }

        public SKColorFilter? DarkThemeFilter { get; }
        public SKImage Image { get; }
        public SKColorFilter? LightThemeFilter { get; }
        public SKPoint[] TextureCoordinates { get; }

        public void Dispose()
        {
            DarkThemeFilter?.Dispose();
            LightThemeFilter?.Dispose();
            Image.Dispose();
        }
    }

    private sealed class MeshData(AppleMusicPinchVertex[] vertices, ushort[] indices)
    {
        public ushort[] Indices { get; } = indices;
        public SKPoint[] Positions { get; } = new SKPoint[vertices.Length];
        public int TextureHeight { get; set; } = -1;
        public SKPoint[] TextureCoordinates { get; } = new SKPoint[vertices.Length];
        public int TextureWidth { get; set; } = -1;
        public AppleMusicPinchVertex[] Vertices { get; } = vertices;
    }
}
