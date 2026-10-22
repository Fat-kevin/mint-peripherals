namespace DeviceCenterPreview.App.Services;

public enum NotificationSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>应用内统一通知：同一个事件既进 InfoBar，也进托盘气泡。</summary>
public sealed record AppNotification(
    string Title,
    string Message,
    NotificationSeverity Severity = NotificationSeverity.Informational);
