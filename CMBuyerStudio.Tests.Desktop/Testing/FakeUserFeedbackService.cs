using CMBuyerStudio.Desktop.Feedback;

namespace CMBuyerStudio.Tests.Desktop.Testing;

public sealed class FakeUserFeedbackService : IUserFeedbackService
{
    private readonly Queue<bool> _confirmationResults = new();

    public event EventHandler<ToastNotification>? ToastNotified;

    public List<(string Message, string Title)> ConfirmCalls { get; } = [];

    public List<ToastNotification> Notifications { get; } = [];

    public bool Confirm(string message, string title = "Confirm")
    {
        ConfirmCalls.Add((message, title));

        if (_confirmationResults.Count == 0)
        {
            return true;
        }

        return _confirmationResults.Dequeue();
    }

    public void NotifySuccess(string message, int durationMs = 2500)
    {
        var toast = new ToastNotification(message, ToastNotificationKind.Success, durationMs);
        Notifications.Add(toast);
        ToastNotified?.Invoke(this, toast);
    }

    public void EnqueueConfirmResult(bool result)
    {
        _confirmationResults.Enqueue(result);
    }
}
