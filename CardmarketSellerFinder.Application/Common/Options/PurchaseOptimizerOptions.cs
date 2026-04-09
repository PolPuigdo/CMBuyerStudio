using System.ComponentModel.DataAnnotations;

namespace CMBuyerStudio.Application.Common.Options;

public sealed class PurchaseOptimizerOptions
{
    public const string SectionName = "Solver";

    [Range(1, 100)]
    public int CandidateTopCheapestPerCard { get; init; } = 10;

    [Range(1, 100)]
    public int CandidateTopEffectivePerCard { get; init; } = 10;

    [Range(1, 500)]
    public int CandidatePoolMin { get; init; } = 40;

    [Range(1, 500)]
    public int CandidatePoolMax { get; init; } = 80;

    [Range(1, 5000)]
    public int BeamWidth { get; init; } = 300;

    [Range(0, 1000)]
    public decimal BeamAlpha { get; init; } = 1m;

    [Range(0, 1000)]
    public decimal BeamBeta { get; init; } = 1m;

    [Range(1, 20)]
    public int ExactMaxK { get; init; } = 7;

    public bool EnableFinalCostRefine { get; init; } = true;

    [Range(1, 240)]
    public int SolverTimeBudgetMinutes { get; init; } = 10;
}
