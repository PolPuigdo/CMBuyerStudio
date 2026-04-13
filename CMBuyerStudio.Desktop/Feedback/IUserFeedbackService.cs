namespace CMBuyerStudio.Desktop.Feedback;

public interface IUserFeedbackService
{
    event EventHandler<ToastNotification>? ToastNotified;

    bool Confirm(string message, string title = "Confirm");

    void NotifySuccess(string message, int durationMs = 2500);
}
