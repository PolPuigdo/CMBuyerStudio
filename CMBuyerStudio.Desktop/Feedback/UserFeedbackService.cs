using System.Windows;

namespace CMBuyerStudio.Desktop.Feedback;

public sealed class UserFeedbackService : IUserFeedbackService
{
    public event EventHandler<ToastNotification>? ToastNotified;

    public bool Confirm(string message, string title = "Confirm")
    {
        var result = MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }

    public void NotifySuccess(string message, int durationMs = 2500)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        ToastNotified?.Invoke(
            this,
            new ToastNotification(message, ToastNotificationKind.Success, durationMs));
    }
}
