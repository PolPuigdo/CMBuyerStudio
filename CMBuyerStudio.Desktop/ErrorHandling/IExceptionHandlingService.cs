namespace CMBuyerStudio.Desktop.ErrorHandling;

public interface IExceptionHandlingService
{
    void Handle(Exception exception, string source, bool isFatal = false);
}
