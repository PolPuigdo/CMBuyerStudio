using CMBuyerStudio.Application.Models;

namespace CMBuyerStudio.Application.Abstractions;

public interface IHtmlReportGenerator
{
    Task<GeneratedHtmlReport> GenerateAsync(
        HtmlReportRequest request,
        CancellationToken cancellationToken = default);
}
