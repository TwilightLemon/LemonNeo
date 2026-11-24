using Avalonia.Threading;

namespace LemonNeo.Desktop.Services;

public enum NotificationLevel
{
    Information,
    Success,
    Warning,
    Error
}

internal sealed record DesktopNotification(string Message, NotificationLevel Level);

public sealed class DesktopNotificationService
{
    internal event Action<DesktopNotification>? Published;

    public void Show(string message, NotificationLevel level = NotificationLevel.Information)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var notification = new DesktopNotification(message, level);
        if (Dispatcher.UIThread.CheckAccess())
            Published?.Invoke(notification);
        else
            Dispatcher.UIThread.Post(() => Published?.Invoke(notification));
    }
}
