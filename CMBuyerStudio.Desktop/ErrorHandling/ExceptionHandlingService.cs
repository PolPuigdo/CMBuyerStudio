using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows;

namespace CMBuyerStudio.Desktop.ErrorHandling;

public sealed class ExceptionHandlingService : IExceptionHandlingService
{
    private readonly IExceptionLogWriter _exceptionLogWriter;
    private readonly IErrorDialogService _errorDialogService;
    private int _isHandlingException;

    public ExceptionHandlingService(
        IExceptionLogWriter exceptionLogWriter,
        IErrorDialogService errorDialogService)
    {
        _exceptionLogWriter = exceptionLogWriter;
        _errorDialogService = errorDialogService;
    }

    public void Handle(Exception exception, string source, bool isFatal = false)
    {
        if (exception is null)
        {
            return;
        }

        if (exception is OperationCanceledException && !isFatal)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _isHandlingException, 1, 0) != 0)
        {
            WriteFallback(exception, source, isFatal);
            return;
        }

        try
        {
            var occurredAt = DateTimeOffset.Now;
            string? logPath = null;

            try
            {
                logPath = _exceptionLogWriter.Write(exception, source, isFatal, occurredAt);
            }
            catch (Exception logException)
            {
                WriteFallback(logException, $"{source}.LogWrite", false);
            }

            var request = BuildDialogRequest(exception, source, isFatal, occurredAt, logPath);

            try
            {
                _errorDialogService.Show(request);
            }
            catch (Exception dialogException)
            {
                WriteFallback(dialogException, $"{source}.DialogShow", false);
                ShowFallbackMessageBox(request);
            }

            if (isFatal)
            {
                ShutdownApplicationSafely();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isHandlingException, 0);
        }
    }

    private static ErrorDialogRequest BuildDialogRequest(
        Exception exception,
        string source,
        bool isFatal,
        DateTimeOffset occurredAt,
        string? logPath)
    {
        var title = isFatal ? "Unexpected fatal error" : "Unexpected error";
        var closeButtonText = isFatal ? "Close application" : "Close";

        var summaryBuilder = new StringBuilder();
        summaryBuilder.AppendLine(isFatal
            ? "An unexpected error occurred. The application will close after you dismiss this dialog."
            : "An unexpected error occurred. You can continue using the application.");

        if (!string.IsNullOrWhiteSpace(exception.Message))
        {
            summaryBuilder.AppendLine();
            summaryBuilder.Append("Error: ").Append(exception.Message.Trim());
        }

        if (!string.IsNullOrWhiteSpace(logPath))
        {
            summaryBuilder.AppendLine();
            summaryBuilder.AppendLine();
            summaryBuilder.Append("Log file: ").Append(logPath);
        }

        var detailsBuilder = new StringBuilder();
        detailsBuilder.Append("Timestamp: ").AppendLine(occurredAt.ToString("O"));
        detailsBuilder.Append("Source: ").AppendLine(source);
        detailsBuilder.Append("Fatal: ").AppendLine(isFatal ? "Yes" : "No");
        detailsBuilder.AppendLine();
        detailsBuilder.AppendLine(exception.ToString());

        return new ErrorDialogRequest(
            title,
            summaryBuilder.ToString(),
            detailsBuilder.ToString(),
            closeButtonText,
            logPath,
            isFatal);
    }

    private static void ShutdownApplicationSafely()
    {
        var application = System.Windows.Application.Current;
        if (application is null)
        {
            return;
        }

        if (application.Dispatcher.CheckAccess())
        {
            application.Shutdown();
            return;
        }

        application.Dispatcher.Invoke(() => application.Shutdown());
    }

    private static void ShowFallbackMessageBox(ErrorDialogRequest request)
    {
        try
        {
            MessageBox.Show(
                request.Summary,
                request.Title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Swallow to avoid recursive failures while already handling exceptions.
        }
    }

    private static void WriteFallback(Exception exception, string source, bool isFatal)
    {
        try
        {
            var message = $"[{DateTimeOffset.Now:O}] source={source} fatal={isFatal}: {exception}";
            Debug.WriteLine(message);
            Console.Error.WriteLine(message);
        }
        catch
        {
            // Intentionally ignored to avoid recursive failures.
        }
    }
}
