using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Desktop.Controls.Shared;
using LemonNeo.Desktop.ViewModels.Library;
using LemonNeo.Desktop.ViewModels.Shared;
using LemonNeo.Desktop.Views.Shell;

namespace LemonNeo.Desktop.Views.Library;

public partial class LibraryView : UserControl
{
    private const string ScrollViewerPart = "PART_ScrollViewer";
    private const double WheelTransitionMilliseconds = 160;

    private readonly TranslateTransform _toolsPosition = new();
    private readonly TranslateTransform _headerPosition = new();
    private ScrollViewer? _trackScroll;
    private ScrollContentPresenter? _trackPresenter;
    private bool _steppedWheelInput;
    private bool _frameRequested;
    private long _animationStart;
    private double _headerSpace;
    private double _collapseDistance;
    private double _collapseFrom;
    private double _collapseTarget;

    public LibraryView()
    {
        InitializeComponent();
        LibraryTools.RenderTransform = _toolsPosition;
        LibraryHeader.RenderTransform = _headerPosition;
        TrackList.TemplateApplied += OnTrackListTemplateApplied;
        TrackList.PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty) UpdateHeaderTarget(animate: false);
        };
        TrackList.AddHandler(PointerWheelChangedEvent, OnTrackListWheel,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        LibraryHeader.SizeChanged += (_, _) => UpdateHeaderSpace();
        CompactPlaylistHeader.SizeChanged += (_, _) => UpdateHeaderSpace();
        CompactPlaylistHeader.PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty) UpdateHeaderSpace();
        };
        _ = new ListReorderBehavior(TrackList, () => DataContext is LibraryViewModel { CanReorderTracks: true },
            (source, target, after) => DataContext is LibraryViewModel vm && source is TrackRow row &&
                                       target is TrackRow destination
                ? vm.ReorderTracksAsync(row, destination, after)
                : Task.CompletedTask);
        TrackSelectionBehavior.Attach(TrackList);
    }

    private void OnTrackListTemplateApplied(object? sender, TemplateAppliedEventArgs args)
    {
        if (_trackScroll is not null)
        {
            _trackScroll.PropertyChanged -= OnTrackScrollChanged;
            _trackScroll.TemplateApplied -= OnTrackScrollTemplateApplied;
        }

        _trackPresenter = null;
        _trackScroll = args.NameScope.Find<ScrollViewer>(ScrollViewerPart);
        if (_trackScroll is null) return;
        _trackScroll.PropertyChanged += OnTrackScrollChanged;
        _trackScroll.TemplateApplied += OnTrackScrollTemplateApplied;
        _trackScroll.Padding = new Thickness(0, _headerSpace, 0, 0);
        UpdateHeaderTarget(animate: false);
    }

    private void OnTrackScrollTemplateApplied(object? sender, TemplateAppliedEventArgs args)
    {
        if (!ReferenceEquals(args.Source, _trackScroll)) return;
        _trackPresenter = args.NameScope.Find<ScrollContentPresenter>("PART_ContentPresenter");
    }

    private void OnTrackListWheel(object? sender, PointerWheelEventArgs args)
    {
        // Touch gestures and fractional precision-wheel events can follow the offset directly.
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
        var space = CompactPlaylistHeader.IsVisible
            ? Math.Max(0, LibraryHeader.Bounds.Height - CompactPlaylistHeader.Bounds.Height)
            : 0;
        if (space == _headerSpace) return;
        _headerSpace = space;

        // Reserve the expanded space inside the scroll extent once. The toolbar moves over it,
        // so collapse never resizes the viewport or writes back to the native offset.
        if (_trackScroll is not null) _trackScroll.Padding = new Thickness(0, space, 0, 0);
        LibraryEmptyState.Margin = new Thickness(0, space, 0, 0);
        UpdateHeaderTarget(animate: false);
    }

    private void UpdateHeaderTarget(bool animate)
    {
        var target = TrackList.IsVisible ? Math.Clamp(_trackScroll?.Offset.Y ?? 0, 0, _headerSpace) : 0;
        if (animate && DataContext is LibraryViewModel { ReduceMotion: false } &&
            IsEffectivelyVisible && TopLevel.GetTopLevel(this) is { } root)
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
        if (progress >= 1 || DataContext is LibraryViewModel { ReduceMotion: true } ||
            !IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } root)
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
        _headerPosition.Y = -12 * progress;
        LibraryHeader.Opacity = 1 - Fade(progress / .65);
        LibraryHeader.IsHitTestVisible = progress < .35;
        LibraryHeader.IsEnabled = LibraryHeader.Opacity > 0;
        CompactPlaylistHeader.Opacity = Fade((progress - .35) / .65);

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

    private void OnLocatePlaying(object? sender, RoutedEventArgs args)
    {
        if (DataContext is LibraryViewModel vm && vm.RevealPlayingTrack() is { } row)
            Dispatcher.UIThread.Post(() =>
            {
                TrackList.ScrollIntoView(row);
                TrackList.Focus();
            }, DispatcherPriority.Loaded);
    }

    private async void OnEditPlaylist(object? sender, RoutedEventArgs args)
    {
        if (this.GetVisualAncestors().OfType<MainWindow>().FirstOrDefault() is { } window)
            await window.EditPlaylistAsync();
    }

    private async void OnDeletePlaylist(object? sender, RoutedEventArgs args)
    {
        if (this.GetVisualAncestors().OfType<MainWindow>().FirstOrDefault() is { } window)
            await window.DeletePlaylistAsync();
    }

    private async void OnMoveTrack(object? sender, RoutedEventArgs args)
    {
        if (this.GetVisualAncestors().OfType<MainWindow>().FirstOrDefault() is { } window)
            await window.MoveTrackAsync();
    }

    private async void OnTrackDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (DataContext is LibraryViewModel { IsSelectionMode: false, SelectedTrack: { } track } vm)
            await vm.PlayAsync(track);
    }

    private async void OnTrackKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter && DataContext is LibraryViewModel
            {
                IsSelectionMode: false, SelectedTrack: { } track
            } vm)
        {
            args.Handled = true;
            await vm.PlayAsync(track);
        }
    }
}
