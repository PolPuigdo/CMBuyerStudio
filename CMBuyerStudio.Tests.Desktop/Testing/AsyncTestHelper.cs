namespace CMBuyerStudio.Tests.Desktop.Testing;

public static class AsyncTestHelper
{
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var startedAt = Environment.TickCount64;

        while (!condition())
        {
            if (Environment.TickCount64 - startedAt > timeoutMs)
            {
                throw new TimeoutException("Condition was not met within the allotted timeout.");
            }

            await Task.Delay(20);
        }
    }
}
