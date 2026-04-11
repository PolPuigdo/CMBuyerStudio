namespace CMBuyerStudio.Application.Models;

public sealed class OptimizationRunProfile
{
    public string Scope { get; init; } = string.Empty;

    public long TotalElapsedMilliseconds { get; init; }

    public IReadOnlyList<OptimizationPhaseProfile> Phases { get; init; } = [];

    public IReadOnlyDictionary<string, long> Counters { get; init; }
        = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Notes { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class OptimizationPhaseProfile
{
    public string Name { get; init; } = string.Empty;

    public long ElapsedMilliseconds { get; init; }

    public IReadOnlyDictionary<string, long> Counters { get; init; }
        = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Notes { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<OptimizationProfileDetail> Details { get; init; } = [];
}

public sealed class OptimizationProfileDetail
{
    public string Name { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, long> Counters { get; init; }
        = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Notes { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
