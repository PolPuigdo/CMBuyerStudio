using CMBuyerStudio.Desktop.Extensions;
using CMBuyerStudio.Tests.Desktop.Testing;

namespace CMBuyerStudio.Tests.Desktop;

public sealed class TaskExtensionsTests
{
    [Fact]
    public async Task ForgetSafe_ReportsUnhandledException()
    {
        var exceptionHandling = new FakeExceptionHandlingService();
        var task = Task.Run(async () =>
        {
            await Task.Delay(30);
            throw new InvalidOperationException("background boom");
        });

        task.ForgetSafe(exceptionHandling, "TaskExtensionsTests.ForgetSafe");

        await AsyncTestHelper.WaitUntilAsync(() => exceptionHandling.HandledExceptions.Count == 1);

        var handled = Assert.Single(exceptionHandling.HandledExceptions);
        Assert.IsType<InvalidOperationException>(handled.Exception);
        Assert.Equal("TaskExtensionsTests.ForgetSafe", handled.Source);
        Assert.False(handled.IsFatal);
    }

    [Fact]
    public async Task ForgetSafe_IgnoresNonFatalCancellation()
    {
        var exceptionHandling = new FakeExceptionHandlingService();
        var canceledTask = Task.FromCanceled(new CancellationToken(canceled: true));

        canceledTask.ForgetSafe(exceptionHandling, "TaskExtensionsTests.CanceledTask");

        await Task.Delay(60);

        Assert.Empty(exceptionHandling.HandledExceptions);
    }
}
