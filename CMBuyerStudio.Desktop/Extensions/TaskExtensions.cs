using CMBuyerStudio.Desktop.ErrorHandling;

namespace CMBuyerStudio.Desktop.Extensions;

public static class TaskExtensions
{
    public static void ForgetSafe(
        this Task task,
        IExceptionHandlingService exceptionHandlingService,
        string source,
        bool isFatal = false)
    {
        if (task is null)
        {
            return;
        }

        if (exceptionHandlingService is null)
        {
            return;
        }

        _ = ObserveAsync(task, exceptionHandlingService, source, isFatal);
    }

    private static async Task ObserveAsync(
        Task task,
        IExceptionHandlingService exceptionHandlingService,
        string source,
        bool isFatal)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!isFatal)
        {
            // Ignore expected cancellations for non-fatal flows.
        }
        catch (Exception exception)
        {
            exceptionHandlingService.Handle(exception, source, isFatal);
        }
    }
}
