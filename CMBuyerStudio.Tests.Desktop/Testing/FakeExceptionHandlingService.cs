using CMBuyerStudio.Desktop.ErrorHandling;

namespace CMBuyerStudio.Tests.Desktop.Testing;

public sealed class FakeExceptionHandlingService : IExceptionHandlingService
{
    public List<(Exception Exception, string Source, bool IsFatal)> HandledExceptions { get; } = [];

    public void Handle(Exception exception, string source, bool isFatal = false)
    {
        HandledExceptions.Add((exception, source, isFatal));
    }
}
