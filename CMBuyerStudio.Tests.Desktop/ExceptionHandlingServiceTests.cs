using CMBuyerStudio.Desktop.ErrorHandling;

namespace CMBuyerStudio.Tests.Desktop;

public sealed class ExceptionHandlingServiceTests
{
    [Fact]
    public void Handle_NonFatal_WritesLogAndShowsDialogWithDetails()
    {
        var logWriter = new RecordingLogWriter(@"C:\temp\desktop-errors-20260420.log");
        var dialogService = new RecordingDialogService();
        var sut = new ExceptionHandlingService(logWriter, dialogService);

        sut.Handle(new InvalidOperationException("boom"), "ExceptionHandlingTests.NonFatal");

        Assert.Equal(1, logWriter.CallCount);

        var request = Assert.Single(dialogService.Requests);
        Assert.False(request.IsFatal);
        Assert.Equal("Close", request.CloseButtonText);
        Assert.Contains("boom", request.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"C:\temp\desktop-errors-20260420.log", request.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ExceptionHandlingTests.NonFatal", request.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Handle_Fatal_UsesFatalDialogConfiguration()
    {
        var logWriter = new RecordingLogWriter(@"C:\temp\desktop-errors-20260420.log");
        var dialogService = new RecordingDialogService();
        var sut = new ExceptionHandlingService(logWriter, dialogService);

        sut.Handle(new Exception("fatal"), "ExceptionHandlingTests.Fatal", isFatal: true);

        var request = Assert.Single(dialogService.Requests);
        Assert.True(request.IsFatal);
        Assert.Equal("Close application", request.CloseButtonText);
        Assert.Contains("will close", request.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Handle_WhenDialogRecursivelyReportsAnotherError_DoesNotReenter()
    {
        var logWriter = new RecordingLogWriter(@"C:\temp\desktop-errors-20260420.log");
        var dialogService = new RecordingDialogService();
        ExceptionHandlingService? sut = null;

        dialogService.OnShow = _ => sut!.Handle(new Exception("nested"), "Nested.Source");
        sut = new ExceptionHandlingService(logWriter, dialogService);

        sut.Handle(new Exception("root"), "Root.Source");

        Assert.Equal(1, logWriter.CallCount);
        Assert.Single(dialogService.Requests);
    }

    [Fact]
    public void Handle_WhenLogWriterFails_StillShowsDialog()
    {
        var logWriter = new ThrowingLogWriter();
        var dialogService = new RecordingDialogService();
        var sut = new ExceptionHandlingService(logWriter, dialogService);

        sut.Handle(new Exception("root"), "Root.Source");

        var request = Assert.Single(dialogService.Requests);
        Assert.DoesNotContain("Log file:", request.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingLogWriter : IExceptionLogWriter
    {
        private readonly string _logPath;

        public RecordingLogWriter(string logPath)
        {
            _logPath = logPath;
        }

        public int CallCount { get; private set; }

        public string Write(Exception exception, string source, bool isFatal, DateTimeOffset occurredAt)
        {
            CallCount++;
            return _logPath;
        }
    }

    private sealed class ThrowingLogWriter : IExceptionLogWriter
    {
        public string Write(Exception exception, string source, bool isFatal, DateTimeOffset occurredAt)
        {
            throw new InvalidOperationException("log write failed");
        }
    }

    private sealed class RecordingDialogService : IErrorDialogService
    {
        public List<ErrorDialogRequest> Requests { get; } = [];

        public Action<ErrorDialogRequest>? OnShow { get; set; }

        public void Show(ErrorDialogRequest request)
        {
            Requests.Add(request);
            OnShow?.Invoke(request);
        }
    }
}
