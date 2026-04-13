namespace CMBuyerStudio.Desktop.Feedback;

public sealed record ToastNotification(string Message, ToastNotificationKind Kind, int DurationMs);
