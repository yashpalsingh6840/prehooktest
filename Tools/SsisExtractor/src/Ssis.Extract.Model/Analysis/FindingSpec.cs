namespace Ssis.Extract.Model.Analysis;

/// <summary>
/// One rule hit from <c>Ssis.Extract.Dtsx.RulesEngine</c> (plan §5.7). Rules are named,
/// produce a location and a human-readable message, and are grouped into the plan's own
/// four categories plus a fifth for the non-determinism rule it calls out separately
/// (§5.7's last bullet, also feeding the manifest in §5.8) -- see <see cref="Category"/>.
/// </summary>
public sealed class FindingSpec
{
    /// <summary>Stable machine-readable id, e.g. "script-task-present", "checkpoints-enabled" -- see docs/report-schema.md for the full rule catalog.</summary>
    public required string RuleId { get; init; }

    /// <summary>"RewriteEffort" | "SemanticRisk" | "EnvironmentCoupling" | "DeadOrSuspicious" | "NonDeterminism" -- the plan's own §5.7 groupings, named for code use (the plan itself uses prose headings).</summary>
    public required string Category { get; init; }

    /// <summary>"Error" (blocks a mechanical rewrite / will silently produce wrong output) | "Warning" (works today, is a known landmine) | "Info" (worth knowing, not inherently a problem).</summary>
    public required string Severity { get; init; }

    public required string PackageName { get; init; }

    /// <summary>Where in the package -- an executable/component path or refId, or "(package)" for a package-level fact. Not a strict schema (locations vary by rule), same "location is a string" pattern <c>UnmappedFragment</c> already uses.</summary>
    public required string Location { get; init; }

    public required string Message { get; init; }
}
