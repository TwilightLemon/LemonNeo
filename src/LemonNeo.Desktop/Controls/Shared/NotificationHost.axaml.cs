using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.Controls.Shared;

public sealed partial class NotificationHost : UserControl
{
    public static readonly StyledProperty<DesktopNotificationService?> SourceProperty =
        AvaloniaProperty.Register<NotificationHost, DesktopNotificationService?>(nameof(Source));

    private const int MaxPending = 5;
    private const double HiddenOffset = 10;
    private readonly Queue<DesktopNotification> _pending = new();
    private readonly DispatcherTimer _timer = new();
    private readonly TranslateTransform _slide;
    private DesktopNotificationService? _subscription;
    private DesktopNotification? _current;
    private bool _fading;

    public NotificationHost()
    {
        InitializeComponent();
        _slide = (TranslateTransform)Card.RenderTransform!;
        _timer.Tick += OnTimerTick;
    }

    public DesktopNotificationService? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(Source);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Subscribe(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty && this.IsAttachedToVisualTree()) Subscribe(Source);
    }

    private void Subscribe(DesktopNotificationService? source)
    {
        if (ReferenceEquals(source, _subscription)) return;
        if (_subscription is not null) _subscription.Published -= OnPublished;
        _timer.Stop();
        _pending.Clear();
        _current = null;
        _fading = false;
        Card.IsVisible = false;
        Card.Opacity = 0;
        _slide.Y = HiddenOffset;
        _subscription = source;
        if (_subscription is not null) _subscription.Published += OnPublished;
    }

    private void OnPublished(DesktopNotification notification)
    {
        if (_current is null)
        {
            Display(notification);
            return;
        }
        if (notification == _current || _pending.Contains(notification)) return;
        if (_pending.Count >= MaxPending) _pending.Dequeue();
        _pending.Enqueue(notification);
    }

    private void Display(DesktopNotification notification)
    {
        _current = notification;
        _fading = false;
        NotificationText.Text = notification.Message;
        NotificationIcon.Classes.Set("success", notification.Level == NotificationLevel.Success);
        NotificationIcon.Classes.Set("warning", notification.Level == NotificationLevel.Warning);
        NotificationIcon.Classes.Set("error", notification.Level == NotificationLevel.Error);
        Card.IsVisible = true;
        Card.Opacity = 1;
        _slide.Y = 0;

        _timer.Interval = TimeSpan.FromSeconds(notification.Level is NotificationLevel.Warning or NotificationLevel.Error ? 6 : 4);
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs args)
    {
        _timer.Stop();
        if (!_fading)
        {
            _fading = true;
            Card.Opacity = 0;
            _slide.Y = HiddenOffset;
            if (this.TryFindResource("Motion.Fast", out var value) && value is TimeSpan duration && duration > TimeSpan.Zero)
            {
                _timer.Interval = duration;
                _timer.Start();
                return;
            }
        }

        Card.IsVisible = false;
        _current = null;
        if (_pending.TryDequeue(out var next)) Display(next);
    }
}
