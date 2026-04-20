using System.Windows;
using CMBuyerStudio.Desktop.Views;

namespace CMBuyerStudio.Desktop.ErrorHandling;

public sealed class ErrorDialogService : IErrorDialogService
{
    public void Show(ErrorDialogRequest request)
    {
        if (request is null)
        {
            return;
        }

        var application = System.Windows.Application.Current;
        if (application is null)
        {
            MessageBox.Show(request.Summary, request.Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (application.Dispatcher.CheckAccess())
        {
            ShowDialog(request, application);
            return;
        }

        application.Dispatcher.Invoke(() => ShowDialog(request, application));
    }

    private static void ShowDialog(ErrorDialogRequest request, System.Windows.Application application)
    {
        var dialog = new ErrorDialogWindow(
            request.Title,
            request.Summary,
            request.Details,
            request.LogPath,
            request.CloseButtonText);

        if (application.MainWindow is { IsLoaded: true } owner && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
    }
}
