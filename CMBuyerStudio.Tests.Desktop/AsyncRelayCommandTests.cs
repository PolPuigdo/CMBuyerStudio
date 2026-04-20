using CMBuyerStudio.Desktop.Commands;
using CMBuyerStudio.Tests.Desktop.Testing;

namespace CMBuyerStudio.Tests.Desktop;

public sealed class AsyncRelayCommandTests
{
    [Fact]
    public async Task Execute_WhenHandlerThrows_ReportsExceptionAndDoesNotCrash()
    {
        var exceptionHandling = new FakeExceptionHandlingService();
        var command = new AsyncRelayCommand(
            async _ =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            },
            _ => true,
            exceptionHandling,
            "AsyncRelayCommandTests.ThrowingHandler");

        command.Execute(null);

        await AsyncTestHelper.WaitUntilAsync(() => exceptionHandling.HandledExceptions.Count == 1);

        var handled = Assert.Single(exceptionHandling.HandledExceptions);
        Assert.IsType<InvalidOperationException>(handled.Exception);
        Assert.Equal("AsyncRelayCommandTests.ThrowingHandler", handled.Source);
        Assert.False(handled.IsFatal);
    }

    [Fact]
    public async Task Execute_DisablesCanExecuteUntilTaskCompletes()
    {
        var exceptionHandling = new FakeExceptionHandlingService();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(
            _ => tcs.Task,
            _ => true,
            exceptionHandling,
            "AsyncRelayCommandTests.LongRunningHandler");

        Assert.True(command.CanExecute(null));

        command.Execute(null);

        Assert.False(command.CanExecute(null));

        tcs.SetResult(true);
        await AsyncTestHelper.WaitUntilAsync(() => command.CanExecute(null));

        Assert.Empty(exceptionHandling.HandledExceptions);
    }
}
