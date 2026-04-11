using CMBuyerStudio.Application.Enums;

namespace CMBuyerStudio.Application.Models;

public sealed class GeneratedHtmlReport
{
    public SellerScopeMode Scope { get; init; }

    public string Path { get; init; } = string.Empty;
}
