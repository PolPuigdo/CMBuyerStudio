namespace CMBuyerStudio.Desktop.ErrorHandling;

public interface IExceptionLogWriter
{
    string Write(Exception exception, string source, bool isFatal, DateTimeOffset occurredAt);
}
