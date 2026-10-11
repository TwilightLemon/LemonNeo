using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace LemonNeo.Desktop.Controls.Shared;

/// <summary>Reorders within a list, with insertion feedback and edge scrolling.</summary>
public sealed class ListReorderBehavior
{
    private readonly Func<bool> _canReorder;
    private readonly ListBox _list;
    private readonly Func<object, object, bool, Task> _move;
    private readonly DispatcherTimer _scrollTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _after;
    private bool _dragging;
    private Point _position;
    private ListBoxItem? _source;
    private object? _sourceItem;
    private Point _start;
    private bool _suppressTap;
    private ListBoxItem? _target;

    public ListReorderBehavior(ListBox list, Func<bool> canReorder, Func<object, object, bool, Task> move)
    {
        _list = list;
        _canReorder = canReorder;
        _move = move;
        list.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, true);
        list.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, true);
        list.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, true);
        list.AddHandler(InputElement.TappedEvent, (_, args) =>
        {
            if (_suppressTap)
            {
                args.Handled = true;
                _suppressTap = false;
            }
        }, RoutingStrategies.Tunnel);
        list.PointerCaptureLost += (_, _) => Reset();
        list.DetachedFromVisualTree += (_, _) => Reset();
        _scrollTimer.Tick += (_, _) => ScrollAtEdge();
    }

    private void OnPressed(object? sender, PointerPressedEventArgs args)
    {
        Reset();
        _suppressTap = false;
        if (!_canReorder() || !args.GetCurrentPoint(_list).Properties.IsLeftButtonPressed ||
            args.Source is not Visual visual) return;
        var ancestors = visual.GetSelfAndVisualAncestors().ToArray();
        if (ancestors.Any(v => v is Button or ToggleButton or ScrollBar)) return;
        _source = ancestors.OfType<ListBoxItem>().FirstOrDefault();
        _sourceItem = _source?.DataContext;
        _start = args.GetPosition(_list);
    }

    private void OnMoved(object? sender, PointerEventArgs args)
    {
        if (_source is null || !args.GetCurrentPoint(_list).Properties.IsLeftButtonPressed) return;
        _position = args.GetPosition(_list);
        if (!_dragging)
        {
            if (Math.Abs(_position.X - _start.X) + Math.Abs(_position.Y - _start.Y) < 6 || !_canReorder()) return;
            _dragging = true;
            _source.Classes.Set("dragging", true);
            args.Pointer.Capture(_list);
            _scrollTimer.Start();
        }

        UpdateTarget();
        args.Handled = true;
    }

    private void UpdateTarget()
    {
        ClearTarget();
        foreach (var item in _list.GetRealizedContainers().OfType<ListBoxItem>())
        {
            if (item.TranslatePoint(default, _list) is not { } origin) continue;
            var bounds = new Rect(origin, item.Bounds.Size);
            if (_position.Y < bounds.Top || _position.Y > bounds.Bottom || _position.X < 0 ||
                _position.X > _list.Bounds.Width) continue;
            _target = item;
            _after = _position.Y >= bounds.Center.Y;
            item.Classes.Set(_after ? "dropAfter" : "dropBefore", true);
            break;
        }
    }

    private void ScrollAtEdge()
    {
        if (_list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroll) return;
        var delta = _position.Y < 36 ? -16 : _position.Y > _list.Bounds.Height - 36 ? 16 : 0;
        if (delta == 0) return;
        scroll.Offset = new Vector(scroll.Offset.X,
            Math.Clamp(scroll.Offset.Y + delta, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
        UpdateTarget();
    }

    private async void OnReleased(object? sender, PointerReleasedEventArgs args)
    {
        var source = _sourceItem;
        var target = _target?.DataContext;
        var after = _after;
        var dragging = _dragging;
        Reset();
        if (!dragging) return;
        _suppressTap = true;
        args.Handled = true;
        args.Pointer.Capture(null);
        if (_canReorder() && source is not null && target is not null) await _move(source, target, after);
    }

    private void ClearTarget()
    {
        _target?.Classes.Set("dropBefore", false);
        _target?.Classes.Set("dropAfter", false);
        _target = null;
    }

    private void Reset()
    {
        _scrollTimer.Stop();
        _source?.Classes.Set("dragging", false);
        ClearTarget();
        _source = null;
        _sourceItem = null;
        _dragging = false;
    }
}