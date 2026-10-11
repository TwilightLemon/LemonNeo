using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;

namespace LemonNeo.Desktop.Controls.Backgrounds;

/// <summary>
/// Avalonia/Skia port of Lyricify Backgrounds' modern Apple Music inspired
/// material. Artwork and optional low-frequency response are host supplied.
/// </summary>
public sealed class AppleMusicMeshBackground : Control
{
    private const float PausedPulseReleaseHalfLifeSeconds = .12f;

    public static readonly StyledProperty<IBackgroundArtworkSource?> ArtworkSourceProperty =
        AvaloniaProperty.Register<AppleMusicMeshBackground, IBackgroundArtworkSource?>(nameof(ArtworkSource));

    public static readonly StyledProperty<ILowFrequencyPulseSource?> PulseSourceProperty =
        AvaloniaProperty.Register<AppleMusicMeshBackground, ILowFrequencyPulseSource?>(nameof(PulseSource));

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<AppleMusicMeshBackground, bool>(nameof(IsPlaying));

    public static readonly StyledProperty<bool> IsAnimationEnabledProperty =
        AvaloniaProperty.Register<AppleMusicMeshBackground, bool>(nameof(IsAnimationEnabled), true);

    public static readonly StyledProperty<bool> IsMeshEnabledProperty =
        AvaloniaProperty.Register<AppleMusicMeshBackground, bool>(nameof(IsMeshEnabled), true);

    public static readonly StyledProperty<int> FrameRateLimitProperty =
        AvaloniaProperty.Register<AppleMusicMeshBackground, int>(nameof(FrameRateLimit), -1,
            coerce: (_, value) => Math.Clamp(value, -1, 240));

    public static readonly StyledProperty<double> RenderScaleProperty =
        AvaloniaProperty.Register<AppleMusicMeshBackground, double>(nameof(RenderScale), 1d,
            coerce: (_, value) => double.IsFinite(value) ? Math.Clamp(value, .125d, 1d) : 1d);

    public static readonly StyledProperty<double> RotationScaleProperty =
        AvaloniaProperty.Register<AppleMusicMeshBackground, double>(nameof(RotationScale), 1d,
            coerce: (_, value) => CoerceSettingScale(value));

    public static readonly StyledProperty<double> BassPulseScaleProperty =
        AvaloniaProperty.Register<AppleMusicMeshBackground, double>(nameof(BassPulseScale), 1d,
            coerce: (_, value) => CoerceSettingScale(value));

    public static readonly StyledProperty<double> BlurScaleProperty =
        AvaloniaProperty.Register<AppleMusicMeshBackground, double>(nameof(BlurScale), 1d,
            coerce: (_, value) => CoerceSettingScale(value));

    public static readonly StyledProperty<int> PresetSlotProperty =
        AvaloniaProperty.Register<AppleMusicMeshBackground, int>(nameof(PresetSlot), -1,
            coerce: (_, value) => Math.Clamp(value, -1, 4));

    private readonly AppleMusicMeshRenderer _renderer = new();
    private Action<TimeSpan>? _animationFrameCallback;
    private long _animationFrameGeneration;
    private bool _animationFramePending;
    private TopLevel? _animationRoot;
    private double _animationTime;
    private bool _attached;
    private CancellationTokenSource? _decodeCancellation;
    private long _decodeGeneration;
    private long _lastFrameTimestamp;
    private long _lastPulseTimestamp;
    private float _pulse;
    private float _pulseTarget;
    private IBackgroundArtworkSource? _subscribedArtworkSource;

    public AppleMusicMeshBackground()
    {
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
    }

    static AppleMusicMeshBackground()
    {
        AffectsRender<AppleMusicMeshBackground>(
            IsMeshEnabledProperty,
            IsAnimationEnabledProperty,
            RenderScaleProperty,
            RotationScaleProperty,
            BassPulseScaleProperty,
            BlurScaleProperty,
            PresetSlotProperty);
    }

    public IBackgroundArtworkSource? ArtworkSource
    {
        get => GetValue(ArtworkSourceProperty);
        set => SetValue(ArtworkSourceProperty, value);
    }

    public ILowFrequencyPulseSource? PulseSource
    {
        get => GetValue(PulseSourceProperty);
        set => SetValue(PulseSourceProperty, value);
    }

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public bool IsAnimationEnabled
    {
        get => GetValue(IsAnimationEnabledProperty);
        set => SetValue(IsAnimationEnabledProperty, value);
    }

    public bool IsMeshEnabled
    {
        get => GetValue(IsMeshEnabledProperty);
        set => SetValue(IsMeshEnabledProperty, value);
    }

    /// <summary>-1 follows the display refresh rate, 0 disables continuous rendering.</summary>
    public int FrameRateLimit
    {
        get => GetValue(FrameRateLimitProperty);
        set => SetValue(FrameRateLimitProperty, value);
    }

    public double RenderScale
    {
        get => GetValue(RenderScaleProperty);
        set => SetValue(RenderScaleProperty, value);
    }

    public double RotationScale
    {
        get => GetValue(RotationScaleProperty);
        set => SetValue(RotationScaleProperty, value);
    }

    public double BassPulseScale
    {
        get => GetValue(BassPulseScaleProperty);
        set => SetValue(BassPulseScaleProperty, value);
    }

    public double BlurScale
    {
        get => GetValue(BlurScaleProperty);
        set => SetValue(BlurScaleProperty, value);
    }

    /// <summary>-1 selects a stable random slot for this control instance.</summary>
    public int PresetSlot
    {
        get => GetValue(PresetSlotProperty);
        set => SetValue(PresetSlotProperty, value);
    }

    public event EventHandler<BackgroundArtworkFailedEventArgs>? ArtworkFailed;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!_renderer.HasArtwork || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var frame = new AppleMusicMeshRenderer.RenderFrame(
            (float)Bounds.Width,
            (float)Bounds.Height,
            1f,
            1f,
            1d,
            RenderScale,
            RotationScale,
            BassPulseScale,
            BlurScale,
            (float)_animationTime,
            _pulse,
            PresetSlot,
            IsMeshEnabled,
            IsAnimationEnabled && IsPlaying,
            ActualThemeVariant == ThemeVariant.Dark);
        context.Custom(new MeshDrawOperation(new Rect(Bounds.Size), _renderer, frame));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ArtworkSourceProperty)
        {
            RefreshArtworkSourceSubscription();
            QueueArtworkRefresh();
        }

        if (change.Property == IsVisibleProperty)
        {
            RefreshArtworkSourceSubscription();
            QueueArtworkRefresh();
        }

        if (change.Property == IsPlayingProperty || change.Property == IsAnimationEnabledProperty ||
            change.Property == FrameRateLimitProperty || change.Property == IsVisibleProperty)
            RefreshAnimation();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _animationRoot = TopLevel.GetTopLevel(this);
        if (_animationRoot is not null) _animationRoot.PropertyChanged += OnTopLevelPropertyChanged;
        RefreshArtworkSourceSubscription();
        QueueArtworkRefresh();
        RefreshAnimation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        if (_animationRoot is not null) _animationRoot.PropertyChanged -= OnTopLevelPropertyChanged;
        _animationRoot = null;
        RefreshArtworkSourceSubscription();
        StopAnimation();
        ResetPulse();
        CancelArtworkDecode();
        _renderer.ClearArtwork();
        base.OnDetachedFromVisualTree(e);
    }

    private static double CoerceSettingScale(double value)
    {
        return double.IsFinite(value) ? Math.Clamp(value, 0d, 10d) : 1d;
    }

    private void RefreshArtworkSourceSubscription()
    {
        var next = _attached && IsVisible ? ArtworkSource : null;
        if (ReferenceEquals(_subscribedArtworkSource, next)) return;
        if (_subscribedArtworkSource is not null)
            _subscribedArtworkSource.ArtworkChanged -= OnArtworkChanged;
        _subscribedArtworkSource = next;
        if (_subscribedArtworkSource is not null)
            _subscribedArtworkSource.ArtworkChanged += OnArtworkChanged;
    }

    private void OnArtworkChanged(object? sender, EventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess()) QueueArtworkRefresh();
        else Dispatcher.UIThread.Post(QueueArtworkRefresh);
    }

    private void QueueArtworkRefresh()
    {
        var source = _subscribedArtworkSource;
        var artwork = source?.CurrentArtwork;
        CancelArtworkDecode();
        var generation = ++_decodeGeneration;
        if (!_attached || artwork is null)
        {
            _renderer.ClearArtwork();
            RefreshAnimation();
            InvalidateVisual();
            return;
        }

        _decodeCancellation = new CancellationTokenSource();
        _ = DecodeArtworkAsync(artwork, generation, _decodeCancellation.Token);
    }

    private async Task DecodeArtworkAsync(BackgroundArtworkFrame artwork, long generation,
        CancellationToken cancellationToken)
    {
        AppleMusicMeshRenderer.DecodedArtwork? image = null;
        try
        {
            image = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decoded = AppleMusicMeshRenderer.DecodeArtwork(artwork);
                if (cancellationToken.IsCancellationRequested) decoded.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                return decoded;
            }, cancellationToken);

            if (cancellationToken.IsCancellationRequested || generation != _decodeGeneration || !_attached) return;
            _renderer.SetArtwork(image, artwork.Id);
            image = null;
            RefreshAnimation();
            InvalidateVisual();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
                ArtworkFailed?.Invoke(this, new BackgroundArtworkFailedEventArgs(exception));
        }
        finally
        {
            image?.Dispose();
        }
    }

    private void CancelArtworkDecode()
    {
        _decodeCancellation?.Cancel();
        _decodeCancellation?.Dispose();
        _decodeCancellation = null;
    }

    private void RefreshAnimation()
    {
        StopAnimation();
        InvalidateVisual();
        if (!_attached || !IsEffectivelyVisible || !IsAnimationEnabled || FrameRateLimit == 0 ||
            !_renderer.HasArtwork || _animationRoot is not { IsVisible: true } ||
            _animationRoot is Window { WindowState: WindowState.Minimized })
        {
            ResetPulse();
            return;
        }

        // After pausing, keep rendering only until the last bass pulse has eased out.
        if (!IsPlaying && _pulse <= 0f && _pulseTarget <= 0f) return;

        var generation = ++_animationFrameGeneration;
        _animationFrameCallback = _ => OnAnimationFrame(generation);
        _lastFrameTimestamp = 0;
        _lastPulseTimestamp = 0;
        RequestAnimationFrame();
    }

    private void StopAnimation()
    {
        ++_animationFrameGeneration;
        _animationFramePending = false;
        _animationFrameCallback = null;
        _lastFrameTimestamp = 0;
        _lastPulseTimestamp = 0;
    }

    private void ResetPulse()
    {
        _pulse = 0f;
        _pulseTarget = 0f;
    }

    private void RequestAnimationFrame()
    {
        if (_animationFramePending || _animationRoot is null || _animationFrameCallback is null) return;
        _animationFramePending = true;
        _animationRoot.RequestAnimationFrame(_animationFrameCallback);
    }

    private void OnAnimationFrame(long generation)
    {
        if (generation != _animationFrameGeneration) return;
        _animationFramePending = false;
        var now = Stopwatch.GetTimestamp();
        var frameRate = FrameRateLimit < 0 ? 60 : FrameRateLimit;
        var minimumTicks = frameRate > 0 ? Math.Max(1, Stopwatch.Frequency / frameRate) : 0;
        if (_lastFrameTimestamp != 0 && now - _lastFrameTimestamp < minimumTicks)
        {
            RequestAnimationFrame();
            return;
        }

        UpdatePulse(now);
        if (IsPlaying && _lastFrameTimestamp != 0)
            _animationTime += (now - _lastFrameTimestamp) / (double)Stopwatch.Frequency;
        _lastFrameTimestamp = now;
        InvalidateVisual();
        if (!IsPlaying && _pulse <= 0f)
        {
            StopAnimation();
            return;
        }

        RequestAnimationFrame();
    }

    private void UpdatePulse(long timestamp)
    {
        var seconds = _lastPulseTimestamp == 0
            ? 1f / 60f
            : (float)Math.Clamp((timestamp - _lastPulseTimestamp) / (double)Stopwatch.Frequency,
                1d / 240d, .25d);
        _lastPulseTimestamp = timestamp;

        if (!IsPlaying)
        {
            _pulseTarget = 0f;
            var release = 1f - MathF.Pow(.5f, seconds / PausedPulseReleaseHalfLifeSeconds);
            _pulse += (0f - _pulse) * release;
            if (_pulse < .0001f) _pulse = 0f;
            return;
        }

        var sample = 0f;
        if (PulseSource?.TryGetLowFrequencyPulse(out var value) == true)
            sample = Math.Clamp(value, 0f, 1f);
        var decay = MathF.Pow(.98f, seconds * 60f);
        _pulseTarget = MathF.Max(sample, _pulseTarget * decay);
        var follow = 1f - MathF.Pow(.5f, seconds * 60f);
        _pulse += (_pulseTarget - _pulse) * follow;
        if (_pulse < .0001f && _pulseTarget < .0001f)
        {
            _pulse = 0f;
            _pulseTarget = 0f;
        }
    }

    private void OnTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == IsVisibleProperty || change.Property == Window.WindowStateProperty)
            RefreshAnimation();
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs args)
    {
        InvalidateVisual();
    }

    private sealed class MeshDrawOperation(
        Rect bounds,
        AppleMusicMeshRenderer renderer,
        AppleMusicMeshRenderer.RenderFrame frame) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public bool HitTest(Point point) => false;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose()
        {
        }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var matrix = canvas.TotalMatrix;
            var scaleX = MathF.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY);
            var scaleY = MathF.Sqrt(matrix.ScaleY * matrix.ScaleY + matrix.SkewX * matrix.SkewX);
            canvas.Save();
            try
            {
                // Mesh presets deliberately extend past the unit square. Avalonia uses
                // Bounds for invalidation, but custom Skia drawing still needs an explicit
                // clip to keep those vertices out of neighboring controls.
                canvas.ClipRect(new SKRect(0, 0, frame.Width, frame.Height),
                    SKClipOperation.Intersect, false);
                renderer.Render(canvas, lease.GrContext, frame with
                {
                    DeviceScaleX = MathF.Max(scaleX, .01f),
                    DeviceScaleY = MathF.Max(scaleY, .01f),
                    Opacity = lease.CurrentOpacity
                });
            }
            finally
            {
                canvas.Restore();
            }
        }
    }
}
