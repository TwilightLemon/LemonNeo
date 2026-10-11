using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace LemonNeo.Desktop.Controls.Playback;

public sealed class AudioVisualizer : Control
{
    private const double EdgeFadeFraction = .08;

    public static readonly StyledProperty<IAudioVisualizerSource?> SourceProperty =
        AvaloniaProperty.Register<AudioVisualizer, IAudioVisualizerSource?>(nameof(Source));

    public static readonly StyledProperty<int> StripCountProperty =
        AvaloniaProperty.Register<AudioVisualizer, int>(nameof(StripCount), 36,
            coerce: (_, value) => Math.Clamp(value, 2, 256));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<AudioVisualizer, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<AudioVisualizer, bool>(nameof(IsPlaying));

    private Action<TimeSpan>? _animationFrameCallback;
    private long _animationFrameGeneration;
    private bool _animationFramePending;
    private TopLevel? _animationRoot;
    private bool _attached;
    private float[] _bars = new float[36];
    private Point[] _points = new Point[36];
    private AudioVisualizerProcessor? _processor;

    static AudioVisualizer()
    {
        AffectsRender<AudioVisualizer>(FillProperty);
    }

    public IAudioVisualizerSource? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public int StripCount
    {
        get => GetValue(StripCountProperty);
        set => SetValue(StripCountProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Fill is not { } fill || Bounds.Width <= 0 || Bounds.Height <= 0 || _bars.Length < 2) return;

        Draw(context, fill, false);
        using (context.PushOpacity(.6)) Draw(context, fill, true);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StripCountProperty)
        {
            _bars = new float[StripCount];
            _points = new Point[StripCount];
        }

        if (change.Property == SourceProperty || change.Property == StripCountProperty ||
            change.Property == IsPlayingProperty || change.Property == IsVisibleProperty)
            RefreshProcessor();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _animationRoot = TopLevel.GetTopLevel(this);
        if (_animationRoot is not null) _animationRoot.PropertyChanged += OnTopLevelPropertyChanged;
        RefreshProcessor();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        if (_animationRoot is not null) _animationRoot.PropertyChanged -= OnTopLevelPropertyChanged;
        StopProcessor();
        _animationRoot = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void RefreshProcessor()
    {
        StopProcessor();
        if (!_attached || !IsEffectivelyVisible || !IsPlaying || Source is not { } source ||
            _animationRoot is not { IsVisible: true } ||
            _animationRoot is Window { WindowState: WindowState.Minimized }) return;
        _processor = new AudioVisualizerProcessor(source, StripCount);
        var generation = ++_animationFrameGeneration;
        _animationFrameCallback = _ => OnAnimationFrame(generation);
        _processor.Start();
        RequestAnimationFrame();
    }

    private void OnTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == IsVisibleProperty || change.Property == Window.WindowStateProperty)
            RefreshProcessor();
    }

    private void StopProcessor()
    {
        _processor?.Dispose();
        _processor = null;
        ++_animationFrameGeneration;
        _animationFramePending = false;
        _animationFrameCallback = null;
        _bars.AsSpan().Clear();
        InvalidateVisual();
    }

    private void RequestAnimationFrame()
    {
        if (_animationFramePending || _processor is null || _animationRoot is null ||
            _animationFrameCallback is null) return;
        _animationFramePending = true;
        _animationRoot.RequestAnimationFrame(_animationFrameCallback);
    }

    private void OnAnimationFrame(long generation)
    {
        if (generation != _animationFrameGeneration) return;
        _animationFramePending = false;
        if (_processor is null) return;
        _processor.CopyCurrent(_bars);
        InvalidateVisual();
        RequestAnimationFrame();
    }

    private void Draw(DrawingContext context, IBrush fill, bool reversed)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        var last = _bars.Length - 1;
        for (var i = 0; i <= last; i++)
        {
            var x = (reversed ? i : last - i) * width / last;
            var edgeProgress = Math.Clamp(Math.Min(i, last - i) / (last * EdgeFadeFraction), 0, 1);
            var edgeEnvelope = edgeProgress * edgeProgress * (3 - 2 * edgeProgress);
            var y = height * (1 - Math.Clamp(_bars[i], 0, 1) * edgeEnvelope);
            _points[i] = new Point(x, y);
        }

        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(_points[0], true);
            for (var i = 1; i < last; i++)
            {
                var midpoint = new Point(
                    (_points[i].X + _points[i + 1].X) * .5,
                    (_points[i].Y + _points[i + 1].Y) * .5);
                path.QuadraticBezierTo(_points[i], midpoint, true);
            }

            path.LineTo(_points[last], true);
            path.LineTo(new Point(_points[last].X, height), true);
            path.LineTo(new Point(_points[0].X, height), true);
            path.EndFigure(true);
        }

        context.DrawGeometry(fill, null, geometry);
    }
}
