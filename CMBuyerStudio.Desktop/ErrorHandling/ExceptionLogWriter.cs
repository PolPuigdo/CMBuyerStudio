using System.IO;
using System.Text;
using CMBuyerStudio.Application.Abstractions;

namespace CMBuyerStudio.Desktop.ErrorHandling;

public sealed class ExceptionLogWriter : IExceptionLogWriter
{
    private readonly IAppPaths _appPaths;

    public ExceptionLogWriter(IAppPaths appPaths)
    {
        _appPaths = appPaths;
    }

    public string Write(Exception exception, string source, bool isFatal, DateTimeOffset occurredAt)
    {
        Directory.CreateDirectory(_appPaths.LogsPath);

        var path = Path.Combine(_appPaths.LogsPath, $"desktop-errors-{occurredAt:yyyyMMdd}.log");
        var payload = BuildPayload(exception, source, isFatal, occurredAt);

        File.AppendAllText(path, payload, Encoding.UTF8);
        return path;
    }

    private static string BuildPayload(Exception exception, string source, bool isFatal, DateTimeOffset occurredAt)
    {
        var builder = new StringBuilder();
        builder.AppendLine("============================================================");
        builder.Append("Timestamp: ").AppendLine(occurredAt.ToString("O"));
        builder.Append("Source: ").AppendLine(source);
        builder.Append("Fatal: ").AppendLine(isFatal ? "Yes" : "No");
        builder.Append("ExceptionType: ").AppendLine(exception.GetType().FullName ?? exception.GetType().Name);
        builder.Append("Message: ").AppendLine(exception.Message);
        builder.AppendLine("Details:");
        builder.AppendLine(exception.ToString());
        builder.AppendLine();

        return builder.ToString();
    }
}
