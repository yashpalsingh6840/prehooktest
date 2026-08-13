namespace Ssis.Extract.Model.Analysis;

/// <summary>One row of <c>inventory.csv</c>/<c>inventory.md</c> (plan §6.1) -- one per package, the counts/classification/coverage a migration-sequencing conversation starts from.</summary>
public sealed class PortfolioInventoryRow
{
    public required string PackageName { get; init; }
    public required ComplexityStats Complexity { get; init; }
    public required double CoveragePercent { get; init; }
    public required int FindingsCount { get; init; }

    /// <summary>The most severe finding's <c>Severity</c> for this package ("Error" > "Warning" > "Info"), or null if it has none.</summary>
    public string? HighestSeverity { get; init; }
}
