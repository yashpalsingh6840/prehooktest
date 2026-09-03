namespace Ssis.Extract.Model.Meta;

/// <summary>
/// <c>_meta.json</c> -- run metadata segregated from the spec content itself (plan §2.3),
/// so a `git diff` on <c>project.spec.json</c>/<c>packages/*.spec.json</c> only ever shows
/// package changes, never noise from "when was this run."
/// </summary>
public sealed class RunMeta
{
    public required string ToolVersion { get; init; }
    public required DateTime RunTimeUtc { get; init; }
    public required string Input { get; init; }
    public required bool Redacted { get; init; }
    public List<string> PackagesProcessed { get; init; } = [];

    /// <summary>
    /// Per-package coverage percentage (plan §7.2), pulled up from each
    /// <c>Package.PackageSpec.Coverage</c> so a caller can see every package's number
    /// without opening each spec.json individually. <c>--fail-under</c> checks against
    /// these same numbers.
    /// </summary>
    public List<PackageCoverageSummary> PackageCoverage { get; init; } = [];
}

public sealed class PackageCoverageSummary
{
    public required string PackageName { get; init; }
    public required double CoveragePercent { get; init; }
}
