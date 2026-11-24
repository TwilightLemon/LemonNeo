using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.Controls.Shared;

internal sealed class CollapsibleListHeader(
    PageHeader header, TextBlock compactTitle, Control toolbar, Control? emptyState, ListBox[] lists)
{
    private const double WheelTransitionMilliseconds = 160;

    private readonly TranslateTransform _toolsPosition = new();
    private readonly TranslateTransform _detailsPosition = new();
    private readonly ScaleTransform _titleScale = new();
    private readonly LinearGradientBrush _wrappedTitleFade = new()
    {
        StartPoint = RelativePoint.TopLeft,
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = [new(Colors.White, 0), new(Colors.White, 1), new(Colors.Transparent, 1)]
    };
    private Control[] _headerDetails = [];
    private TextBlock? _headerTitle;
    private ListBox? _activeList;
    private ScrollViewer? _trackScroll;
    private ScrollContentPresenter? _trackPresenter;
    private bool _steppedWheelInput;
    private bool _frameRequested;
    private long _animationStart;
    private double _headerSpace;
    private double _collapseDistance;
    private double _collapseFrom;
    private double _collapseTarget;

    public static void Attach(PageHeader header, TextBlock compactTitle, Control toolbar, Control? emptyState,
        params ListBox[] lists)
    {
        new CollapsibleListHeader(header, compactTitle, toolbar, emptyState, lists).Connect();
    }

    private void Connect()
    {
        toolbar.RenderTransform = _toolsPosition;
        header.TemplateApplied += OnHeaderTemplateApplied;
        toolbar.PointerWheelChanged += OnHeaderWheel;
        header.PointerWheelChanged += OnHeaderWheel;
        header.SizeChanged += (_, _) => UpdateHeaderSpace();
        compactTitle.SizeChanged += (_, _) => UpdateHeaderSpace();
        compactTitle.PropertyChanged += (_, args) =>
        {
            if (args.Property == Visual.IsVisibleProperty) UpdateHeaderSpace();
        };
        foreach (var list in lists)
        {
            list.TemplateApplied += OnListTemplateApplied;
            list.PropertyChanged += (_, args) =>
            {
                if (args.Property == Visual.IsVisibleProperty) SelectActiveList();
            };
            list.AddHandler(InputElement.PointerWheelChangedEvent, OnListWheel,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        }
        SelectActiveList();
    }

    private void OnHeaderTemplateApplied(object? sender, TemplateAppliedEventArgs args)
    {
        if (!ReferenceEquals(args.Source, header)) return;
        _headerTitle = args.NameScope.Find<TextBlock>("HeaderTitle");
        if (_headerTitle is not null)
        {
            _headerTitle.RenderTransformOrigin = RelativePoint.TopLeft;
            _headerTitle.RenderTransform = _titleScale;
        }

        _headerDetails = new[]
        {
            args.NameScope.Find<Control>("HeaderDescription"),
            args.NameScope.Find<Control>("HeaderSummary"),
            args.NameScope.Find<Control>("HeaderActions")
        }.OfType<Control>().ToArray();
        foreach (var detail in _headerDetails) detail.RenderTransform = _detailsPosition;
        RenderHeader(_collapseDistance);
    }

    private void SelectActiveList()
    {
        _activeList = lists.FirstOrDefault(list => list.IsVisible);
        SetScrollViewer(_activeList?.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(viewer => viewer.Name == "PART_ScrollViewer"));
    }

    private void OnListTemplateApplied(object? sender, TemplateAppliedEventArgs args)
    {
        if (ReferenceEquals(args.Source, _activeList))
            SetScrollViewer(args.NameScope.Find<ScrollViewer>("PART_ScrollViewer"));
    }

    private void SetScrollViewer(ScrollViewer? viewer)
    {
        if (ReferenceEquals(viewer, _trackScroll))
        {
            UpdateHeaderTarget(animate: false);
            return;
        }
        if (_trackScroll is not null)
        {
            _trackScroll.PropertyChanged -= OnTrackScrollChanged;
            _trackScroll.TemplateApplied -= OnScrollTemplateApplied;
        }
        if (_trackPresenter is not null) _trackPresenter.Clip = null;

        _trackScroll = viewer;
        _trackPresenter = viewer?.GetVisualDescendants().OfType<ScrollContentPresenter>()
            .FirstOrDefault(presenter => presenter.Name == "PART_ContentPresenter");
        if (viewer is not null)
        {
            viewer.PropertyChanged += OnTrackScrollChanged;
            viewer.TemplateApplied += OnScrollTemplateApplied;
            viewer.Padding = new Thickness(0, _headerSpace, 0, 0);
        }
        UpdateHeaderTarget(animate: false);
    }

    private void OnScrollTemplateApplied(object? sender, TemplateAppliedEventArgs args)
    {
        if (!ReferenceEquals(args.Source, _trackScroll)) return;
        _trackPresenter = args.NameScope.Find<ScrollContentPresenter>("PART_ContentPresenter");
    }

    private void OnHeaderWheel(object? sender, PointerWheelEventArgs args)
    {
        // A moving toolbar can pass under a stationary pointer while the title expands.
        if (_headerSpace == 0 || _trackPresenter is not { IsEffectivelyEnabled: true } presenter ||
            TopLevel.GetTopLevel(header) is not { } root) return;
        var point = args.GetCurrentPoint(root);
        var forwarded = new PointerWheelEventArgs(presenter, args.Pointer, root, point.Position, args.Timestamp,
            point.Properties, args.KeyModifiers, args.Delta);
        presenter.RaiseEvent(forwarded);
        args.Handled = forwarded.Handled;
    }

    private void OnListWheel(object? sender, PointerWheelEventArgs args)
    {
        // Touch gestures and fractional precision-wheel events follow the offset directly.
        if (!ReferenceEquals(sender, _activeList)) return;
        var delta = Math.Abs(args.Delta.Y);
        _steppedWheelInput = args.Route == RoutingStrategies.Tunnel &&
                             delta >= 1 && Math.Abs(delta - Math.Round(delta)) < 0.001;
    }

    private void OnTrackScrollChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == ScrollViewer.OffsetProperty) UpdateHeaderTarget(_steppedWheelInput);
    }

    private void UpdateHeaderSpace()
    {
        var space = compactTitle.IsVisible ? Math.Max(0, header.Bounds.Height - compactTitle.Bounds.Height) : 0;
        if (space == _headerSpace) return;
        _headerSpace = space;

        // Reserve the expanded space in the scroll extent while the toolbar moves over it.
        if (_trackScroll is not null) _trackScroll.Padding = new Thickness(0, space, 0, 0);
        if (emptyState is not null) emptyState.Margin = new Thickness(0, space, 0, 0);
        UpdateHeaderTarget(animate: false);
    }

    private void UpdateHeaderTarget(bool animate)
    {
        var target = _activeList is not null ? Math.Clamp(_trackScroll?.Offset.Y ?? 0, 0, _headerSpace) : 0;
        if (animate && header.DataContext is TrackListViewModel { ReduceMotion: false } &&
            header.IsEffectivelyVisible && TopLevel.GetTopLevel(header) is { } root)
        {
            if (target == _collapseTarget) return;
            _collapseTarget = target;
            _collapseFrom = _collapseDistance;
            _animationStart = Stopwatch.GetTimestamp();
            if (!_frameRequested)
            {
                _frameRequested = true;
                root.RequestAnimationFrame(AnimateHeader);
            }
        }
        else
        {
            _collapseTarget = target;
            _animationStart = 0;
            RenderHeader(target);
        }
    }

    private void AnimateHeader(TimeSpan _)
    {
        _frameRequested = false;
        if (_animationStart == 0) return;
        var progress = Math.Min(1, Stopwatch.GetElapsedTime(_animationStart).TotalMilliseconds /
                                  WheelTransitionMilliseconds);
        if (progress >= 1 || header.DataContext is TrackListViewModel { ReduceMotion: true } ||
            !header.IsEffectivelyVisible || TopLevel.GetTopLevel(header) is not { } root)
        {
            _animationStart = 0;
            RenderHeader(_collapseTarget);
            return;
        }

        var remaining = 1 - progress;
        RenderHeader(_collapseTarget + (_collapseFrom - _collapseTarget) * remaining * remaining * remaining);
        _frameRequested = true;
        root.RequestAnimationFrame(AnimateHeader);
    }

    private void RenderHeader(double distance)
    {
        _collapseDistance = distance;
        var progress = _headerSpace > 0 ? distance / _headerSpace : 0;
        _toolsPosition.Y = _headerSpace - distance;
        _detailsPosition.Y = -10 * progress;
        var detailsOpacity = 1 - Fade(progress / .55);
        foreach (var detail in _headerDetails) detail.Opacity = detailsOpacity;
        header.IsHitTestVisible = progress < .35;
        header.IsEnabled = detailsOpacity > 0;

        // Scale the heading in place, then hand off to the single-line title at the compact size.
        var compactOpacity = Fade((progress - .75) / .25);
        compactTitle.Opacity = compactOpacity;
        if (_headerTitle is { } title)
        {
            var scale = 1 + (compactTitle.FontSize / title.FontSize - 1) * Fade(progress / .85);
            _titleScale.ScaleX = _titleScale.ScaleY = scale;
            title.Opacity = 1 - compactOpacity;

            // Fade the lower lines of a wrapped title before they reach the moving toolbar.
            var visibleHeight = (compactTitle.Bounds.Height + _toolsPosition.Y) / scale;
            if (progress is > 0 and < 1 && title.Bounds.Height > visibleHeight)
            {
                _wrappedTitleFade.GradientStops[1].Offset = Math.Max(0, visibleHeight - 18) / title.Bounds.Height;
                _wrappedTitleFade.GradientStops[2].Offset = visibleHeight / title.Bounds.Height;
                title.OpacityMask = _wrappedTitleFade;
            }
            else title.OpacityMask = null;
        }

        // Only a stepped downward wheel scroll can put rows behind the moving toolbar.
        // Clip the leading edge during that brief overlap, keeping the scrollbar usable.
        if (_trackPresenter is not { } presenter) return;
        var top = Math.Max(0, _collapseTarget - distance);
        presenter.Clip = top > 0 && presenter.Bounds.Height > 0
            ? new RectangleGeometry(new Rect(0, top, presenter.Bounds.Width, Math.Max(0, presenter.Bounds.Height - top)))
            : null;
    }

    private static double Fade(double value)
    {
        var t = Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }

}
