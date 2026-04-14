using System.Windows;
using CMBuyerStudio.Desktop.Views;

namespace CMBuyerStudio.Desktop.Feedback;

public sealed class UserFeedbackService : IUserFeedbackService
{
    public event EventHandler<ToastNotification>? ToastNotified;

    public bool Confirm(string message, string title = "Confirm")
    {
        if (System.Windows.Application.Current is null)
        {
            return false;
        }

        if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            return System.Windows.Application.Current.Dispatcher.Invoke(() => Confirm(message, title));
        }

        var dialog = new ConfirmDialogWindow(title, message)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };

        var result = dialog.ShowDialog();
        return result == true;
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
