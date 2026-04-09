using System.ComponentModel.DataAnnotations;

namespace CMBuyerStudio.Application.Common.Options;

public class ShippingCostsOptions
{
    public const string SectionName = "ShippingCosts";

    [Range(0, 1000)]
    public double Default { get; init; } = 3.0;

    public Dictionary<string, double> Countries { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}