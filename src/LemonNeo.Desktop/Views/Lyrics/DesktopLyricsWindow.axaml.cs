using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Desktop.Platform;
using LemonNeo.Desktop.ViewModels.Lyrics;

namespace LemonNeo.Desktop.Views.Lyrics;

public partial class DesktopLyricsWindow : Window
{
    private readonly Cursor _diagonalResize = new(StandardCursorType.TopLeftCorner);
    private readonly Cursor _horizontalResize = new(StandardCursorType.SizeWestEast);
    private readonly Cursor _reverseDiagonalResize = new(StandardCursorType.TopRightCorner);
    private readonly Cursor _verticalResize = new(StandardCursorType.SizeNorthSouth);
    private PixelPoint _resizePointer;
    private PixelPoint _resizePosition;
    private double _resizeScaling;
    private Size _resizeSize;
    private WindowEdge? _resizingEdge;
    private LyricsViewModel? _viewModel;
    private bool _isVertical;

    public DesktopLyricsWindow()
    {
        InitializeComponent();
        DesktopLyricsPlatform.Configure(this);
        AddHandler(PointerPressedEvent, OnResizePressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnResizeMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnResizeReleased, RoutingStrategies.Tunnel);
        PointerCaptureLost += (_, _) => _resizingEdge = null;
        Opened += (_, _) => DesktopLyricsPlatform.OnOpened(this);
        LayoutUpdated += (_, _) => KeepOnScreen();
        Screens.Changed += OnScreensChanged;
        Closed += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnLyricsPreferenceChanged;
            Screens.Changed -= OnScreensChanged;
            if (DataContext is LyricsViewModel vm) vm.SaveDesktopWindowSize(Bounds.Width, Bounds.Height);
            _horizontalResize.Dispose();
            _verticalResize.Dispose();
            _diagonalResize.Dispose();
            _reverseDiagonalResize.Dispose();
        };
    }

    protected override void OnDataContextChanged(EventArgs args)
    {
        base.OnDataContextChanged(args);
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnLyricsPreferenceChanged;
        _viewModel = DataContext as LyricsViewModel;
        if (DataContext is not LyricsViewModel vm) return;
        _viewModel!.PropertyChanged += OnLyricsPreferenceChanged;
        ApplyDisplayMode(vm.IsVerticalDesktopLyrics, false);
        Width = vm.DesktopLyricWindowWidth;
        Height = vm.DesktopLyricWindowHeight;
    }

    private void OnLyricsPreferenceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(LyricsViewModel.IsVerticalDesktopLyrics) && _viewModel is { } vm &&
            _isVertical != vm.IsVerticalDesktopLyrics)
            ApplyDisplayMode(vm.IsVerticalDesktopLyrics, true);
    }

    private void ApplyDisplayMode(bool vertical, bool swapSize)
    {
        var oldWidth = Width;
        var oldHeight = Height;
        _isVertical = vertical;
        MinWidth = vertical ? 110 : 320;
        MinHeight = vertical ? 320 : 110;
        DesktopLayout.ColumnDefinitions = new ColumnDefinitions(vertical ? "40,*" : "*");
        DesktopLayout.RowDefinitions = new RowDefinitions(vertical ? "*,Auto" : "40,*,Auto");
        Grid.SetColumn(DesktopLyricsTimeline, vertical ? 1 : 0);
        Grid.SetRow(DesktopLyricsTimeline, vertical ? 0 : 1);
        Grid.SetColumn(DesktopLyricsError, vertical ? 1 : 0);
        Grid.SetRow(DesktopLyricsError, vertical ? 1 : 2);
        ToolbarLayout.ColumnDefinitions = new ColumnDefinitions(vertical ? "*" : "*,Auto,*");
        ToolbarLayout.RowDefinitions = new RowDefinitions(vertical ? "Auto,Auto,Auto" : "*");
        ToolbarDragHandle.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        ToolbarDragHandle.Margin = vertical ? new Thickness(0, 0, 0, 12) : default;
        Grid.SetColumn(ToolbarButtons, vertical ? 0 : 1);
        Grid.SetRow(ToolbarButtons, vertical ? 1 : 0);
        ToolbarButtons.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        ToolbarButtons.HorizontalAlignment = HorizontalAlignment.Center;
        ToolbarButtons.Margin = vertical ? new Thickness(0, 0, 0, 12) : default;
        Grid.SetColumn(ToolbarActions, vertical ? 0 : 2);
        Grid.SetRow(ToolbarActions, vertical ? 2 : 0);
        ToolbarActions.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        ToolbarActions.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        ToolbarActions.Spacing = vertical ? 8 : 4;
        if (swapSize)
        {
            Width = Math.Max(MinWidth, oldHeight);
            Height = Math.Max(MinHeight, oldWidth);
            _viewModel?.SaveDesktopWindowSize(Width, Height);
        }
    }

    public void ShowNear(Window mainWindow, PixelPoint? savedPosition)
    {
        var screen = Screens.ScreenFromWindow(mainWindow) ?? Screens.Primary;
        if (screen is not null)
            Width = Math.Min(Width, Math.Max(MinWidth, screen.WorkingArea.Width / mainWindow.DesktopScaling - 48));
        // An unowned window stays visible when the main window is minimized.
        Show();
        if (savedPosition is { } position) Position = position;
        else if (screen is not null)
            Position = new PixelPoint(
                screen.WorkingArea.X + (screen.WorkingArea.Width - (int)(Bounds.Width * DesktopScaling)) / 2,
                screen.WorkingArea.Bottom - (int)((Bounds.Height + 48) * DesktopScaling));
        KeepOnScreen();
        // The initial native layout may increase the height to fit the selected lyric rows.
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible) return;
            if (savedPosition is null && screen is not null)
                Position = new PixelPoint(
                    screen.WorkingArea.X + (screen.WorkingArea.Width - (int)(Bounds.Width * DesktopScaling)) / 2,
                    screen.WorkingArea.Bottom - (int)((Bounds.Height + 48) * DesktopScaling));
            KeepOnScreen();
        }, DispatcherPriority.Loaded);
    }

    private void OnScreensChanged(object? sender, EventArgs args)
    {
        KeepOnScreen();
    }

    private void KeepOnScreen()
    {
        if (!IsVisible || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        if (_isVertical)
        {
            var minimumWidth = Math.Ceiling(DesktopLyricsTimeline.CompactContentWidth + 84);
            if (double.IsFinite(minimumWidth) && Math.Abs(MinWidth - Math.Max(110, minimumWidth)) > .5)
                MinWidth = Math.Max(110, minimumWidth);
        }
        else
        {
            var minimumHeight = Math.Ceiling(DesktopLyricsTimeline.CompactContentHeight + 72
                + (DesktopLyricsError.IsVisible ? DesktopLyricsError.DesiredSize.Height : 0));
            if (double.IsFinite(minimumHeight) && Math.Abs(MinHeight - Math.Max(110, minimumHeight)) > .5)
                MinHeight = Math.Max(110, minimumHeight);
        }
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        // macOS desktop coordinates use logical points even on Retina displays; Windows uses scaled pixels.
        var availableWidth = Math.Max(MinWidth, area.Width / DesktopScaling - 48);
        var availableHeight = Math.Max(MinHeight, area.Height / DesktopScaling - 48);
        if (Width > availableWidth) Width = availableWidth;
        if (Height > availableHeight) Height = availableHeight;
        var width = (int)Math.Ceiling(Bounds.Width * DesktopScaling);
        var height = (int)Math.Ceiling(Bounds.Height * DesktopScaling);
        var position = new PixelPoint(Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - width)),
            Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - height)));
        if (Position != position) Position = position;
    }

    private WindowEdge? ResizeEdge(Point point)
    {
        const int grip = 6;
        var left = point.X >= 0 && point.X < grip;
        var right = point.X > Bounds.Width - grip && point.X <= Bounds.Width;
        var top = point.Y >= 0 && point.Y < grip;
        var bottom = point.Y > Bounds.Height - grip && point.Y <= Bounds.Height;
        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => WindowEdge.NorthWest,
            (_, true, true, _) => WindowEdge.NorthEast,
            (true, _, _, true) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.West,
            (_, true, _, _) => WindowEdge.East,
            (_, _, true, _) => WindowEdge.North,
            (_, _, _, true) => WindowEdge.South,
            _ => null
        };
    }

    private void OnResizePressed(object? sender, PointerPressedEventArgs args)
    {
        if (!CanResize || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            ResizeEdge(args.GetPosition(this)) is not { } edge) return;
        args.Handled = true;
        // Captured pointer resizing also works on macOS, where Avalonia.Native's BeginResizeDrag is a no-op.
        _resizingEdge = edge;
        _resizePointer = this.PointToScreen(args.GetPosition(this));
        _resizePosition = Position;
        _resizeSize = Bounds.Size;
        _resizeScaling = DesktopScaling;
        args.Pointer.Capture(this);
    }

    private void OnResizeMoved(object? sender, PointerEventArgs args)
    {
        if (_resizingEdge is { } edge)
        {
            var point = this.PointToScreen(args.GetPosition(this));
            var deltaX = (point.X - _resizePointer.X) / _resizeScaling;
            var deltaY = (point.Y - _resizePointer.Y) / _resizeScaling;
            var left = edge is WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest;
            var right = edge is WindowEdge.East or WindowEdge.NorthEast or WindowEdge.SouthEast;
            var top = edge is WindowEdge.North or WindowEdge.NorthWest or WindowEdge.NorthEast;
            var bottom = edge is WindowEdge.South or WindowEdge.SouthWest or WindowEdge.SouthEast;
            Width = Math.Clamp(_resizeSize.Width + (right ? deltaX : left ? -deltaX : 0), MinWidth, MaxWidth);
            Height = Math.Clamp(_resizeSize.Height + (bottom ? deltaY : top ? -deltaY : 0), MinHeight, MaxHeight);
            Position = new PixelPoint(
                _resizePosition.X + (left ? (int)Math.Round((_resizeSize.Width - Width) * _resizeScaling) : 0),
                _resizePosition.Y + (top ? (int)Math.Round((_resizeSize.Height - Height) * _resizeScaling) : 0));
            args.Handled = true;
            return;
        }

        Cursor = ResizeEdge(args.GetPosition(this)) switch
        {
            WindowEdge.West or WindowEdge.East => _horizontalResize,
            WindowEdge.North or WindowEdge.South => _verticalResize,
            WindowEdge.NorthWest or WindowEdge.SouthEast => _diagonalResize,
            WindowEdge.NorthEast or WindowEdge.SouthWest => _reverseDiagonalResize,
            _ => null
        };
    }

    private void OnResizeReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (_resizingEdge is null) return;
        _resizingEdge = null;
        args.Pointer.Capture(null);
        args.Handled = true;
    }

    private void OnDragWindow(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed || args.Source is not Visual source) return;
        if (source.GetSelfAndVisualAncestors().OfType<Button>().Any()) return;
        args.Handled = true;
        BeginMoveDrag(args);
    }

    private void OnSettingsOpened(object? sender, EventArgs args)
    {
        DesktopLyricsPlatform.SetSettingsInputEnabled(this, true);
        HoverCard.Classes.Set("settingsOpen", true);
    }

    private void OnSettingsClosed(object? sender, EventArgs args)
    {
        DesktopLyricsPlatform.SetSettingsInputEnabled(this, false);
        HoverCard.Classes.Set("settingsOpen", false);
    }

    private void OnCloseWindow(object? sender, RoutedEventArgs args)
    {
        Close();
    }
}
