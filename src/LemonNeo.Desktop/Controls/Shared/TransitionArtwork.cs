using Avalonia;
using Avalonia.Controls;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.Controls.Shared;

/// <summary>A now-playing cover with a live blur-out / blur-in transition.</summary>
public sealed class TransitionArtwork : UserControl
{
    public static readonly StyledProperty<LoadedArtwork?> SourceProperty =
        AvaloniaProperty.Register<TransitionArtwork, LoadedArtwork?>(nameof(Source));
    public static readonly StyledProperty<bool> ReduceMotionProperty =
        AvaloniaProperty.Register<TransitionArtwork, bool>(nameof(ReduceMotion));

    private readonly Artwork _artwork = new();
    private readonly BlurTransitionHost _transition;
    private readonly Border _clip;
    private LoadedArtwork? _displayed;
    private LoadedArtwork? _pending;
    private bool _attached;

    public TransitionArtwork()
    {
        _transition = new BlurTransitionHost(_artwork)
            { FadeOutMilliseconds = 240, FadeInMilliseconds = 320, BlurRadius = 24 };
        _clip = new Border { Child = _transition, ClipToBounds = true, CornerRadius = CornerRadius };
        Content = _clip;
    }

    public LoadedArtwork? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool ReduceMotion
    {
        get => GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_transition is null) return;
        if (change.Property == ReduceMotionProperty) _transition.ReduceMotion = ReduceMotion;
        if (change.Property == CornerRadiusProperty) _clip.CornerRadius = _artwork.CornerRadius = CornerRadius;
        if (change.Property == SourceProperty && _attached) RefreshSource();
    }

    private void RefreshSource()
    {
        _pending?.Dispose();
        // Keep the original bitmap alive through fade-out; no capture, resampling, or DPI conversion.
        _pending = Source?.Retain();
        _transition.Change(() =>
        {
            var previous = _displayed;
            _displayed = _pending;
            _pending = null;
            _artwork.Source = _displayed?.Bitmap;
            previous?.Dispose();
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        RefreshSource();
        // Reopening the lyrics page should show the current cover immediately.
        _transition.Complete();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _transition.Complete();
        _artwork.Source = null;
        _displayed?.Dispose();
        _displayed = null;
        _pending?.Dispose();
        _pending = null;
        base.OnDetachedFromVisualTree(e);
    }
}
