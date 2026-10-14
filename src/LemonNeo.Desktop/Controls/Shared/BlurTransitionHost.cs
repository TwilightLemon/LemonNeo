using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace LemonNeo.Desktop.Controls.Shared;

/// <summary>Blurs the live content out, replaces it while invisible, then brings it into focus.</summary>
internal sealed class BlurTransitionHost : Decorator
{
    private readonly Control _content;
    private readonly Stopwatch _clock = new();
    private readonly List<Visual> _ancestors = [];
    private Action? _pendingChange;
    private Action<TimeSpan>? _frame;
    private bool _fadingOut;
    private bool _reduceMotion;
    private int _generation;
    private double _radius;
    private double _fromRadius;
    private double _fromOpacity;

    public BlurTransitionHost(Control content)
    {
        _content = content;
        Child = content;
    }

    public double FadeOutMilliseconds { get; init; } = 180;
    public double FadeInMilliseconds { get; init; } = 260;
    public double BlurRadius { get; init; } = 12;

    public bool ReduceMotion
    {
        get => _reduceMotion;
        set
        {
            _reduceMotion = value;
            if (value) Complete();
        }
    }

    public void Change(Action change)
    {
        _pendingChange = change;
        var root = TopLevel.GetTopLevel(this);
        if (ReduceMotion || !IsEffectivelyVisible || root is null ||
            root is Window { WindowState: WindowState.Minimized } || Bounds.Width < 1 || Bounds.Height < 1)
        {
            Complete();
            return;
        }

        // Playback ticks and rapid skips replace the pending update without restarting the outgoing phase.
        if (_fadingOut) return;
        _fadingOut = true;
        _fromRadius = _radius;
        _fromOpacity = _content.Opacity;
        _clock.Restart();
        var generation = ++_generation;
        _frame = _ => Tick(root, generation);
        root.RequestAnimationFrame(_frame);
    }

    private void Tick(TopLevel root, int generation)
    {
        if (generation != _generation) return;
        if (!IsEffectivelyVisible || root is Window { WindowState: WindowState.Minimized })
        {
            Complete();
            return;
        }

        var duration = _fadingOut ? FadeOutMilliseconds : FadeInMilliseconds;
        var progress = Math.Clamp(_clock.Elapsed.TotalMilliseconds / duration, 0, 1);
        var eased = progress * progress * (3 - 2 * progress);
        if (_fadingOut)
        {
            SetFrame(_fromRadius + (BlurRadius - _fromRadius) * eased, _fromOpacity * (1 - eased * eased));
            if (progress >= 1)
            {
                _fadingOut = false;
                var change = _pendingChange;
                _pendingChange = null;
                change?.Invoke();
                // Start the incoming phase after the replacement has had a chance to lay out at native size.
                _clock.Restart();
            }
        }
        else
        {
            SetFrame(BlurRadius * (1 - eased), 1 - Math.Pow(1 - eased, 2));
            if (progress >= 1)
            {
                Complete();
                return;
            }
        }

        if (generation == _generation && _frame is not null) root.RequestAnimationFrame(_frame);
    }

    private void SetFrame(double radius, double opacity)
    {
        _radius = radius;
        // Assign Effect each frame so the compositor receives the changed radius, rather than only
        // mutating a nested effect object that may not invalidate the visual on every rendering backend.
        _content.Effect = radius > .01 ? new BlurEffect { Radius = radius } : null;
        _content.Opacity = opacity;
    }

    public void Complete()
    {
        ++_generation;
        _frame = null;
        _clock.Reset();
        _fadingOut = false;
        var change = _pendingChange;
        _pendingChange = null;
        change?.Invoke();
        SetFrame(0, 1);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        foreach (var ancestor in this.GetVisualAncestors())
        {
            _ancestors.Add(ancestor);
            ancestor.PropertyChanged += OnAncestorChanged;
        }
    }

    private void OnAncestorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if ((e.Property == IsVisibleProperty && !IsEffectivelyVisible) ||
            (e.Property == Window.WindowStateProperty && sender is Window { WindowState: WindowState.Minimized }))
            Complete();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Complete();
        foreach (var ancestor in _ancestors) ancestor.PropertyChanged -= OnAncestorChanged;
        _ancestors.Clear();
        base.OnDetachedFromVisualTree(e);
    }
}
