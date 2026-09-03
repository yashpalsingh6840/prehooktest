namespace Ssis.Extract.Model.Analysis;

/// <summary>
/// One obligation the rewrite must satisfy, derived from an already-parsed
/// <c>PackageSpec</c> by <c>Ssis.Extract.Dtsx.ConformanceRulesBuilder</c> -- gate 1 of
/// <c>Migration-Validation-Plan.md</c> ("spec conformance"), and the first consumer of the
/// extractor's own output rather than another producer of it.
///
/// <b>What this is for, since it is easy to mistake for the findings engine:</b>
/// <c>FindingSpec</c> answers "what about this package will hurt during rewrite" (risk).
/// This answers "what must the replacement implement to be a true replacement" (contract).
/// A package with zero findings still has dozens of conformance rules. Gate 1 catches the
/// defect class the plan calls out as surviving code review -- *you forgot this entirely* --
/// as opposed to gate 2's "you implemented it but the logic is wrong."
/// </summary>
public sealed class ConformanceRuleSpec
{
    /// <summary>
    /// Stable, human-readable, deterministic id of the form
    /// <c>&lt;CATEGORY&gt;:&lt;location&gt;</c>, e.g.
    /// <c>TARGET-COLUMN:[dbo].[Employee].FullName</c>. Deliberately readable rather than a
    /// hash: this id is what a reviewer reads in a report and what the rewrite author types
    /// into their implementation claim file, so it has to survive being looked at by a human.
    ///
    /// <b>Stability is a hard requirement, not a nice-to-have</b> -- an implementation claim
    /// file is keyed by these ids and is hand-maintained over the life of a migration, so an
    /// id that changes when someone nudges a package in the designer would silently orphan
    /// every claim against it. Ids are therefore derived only from things that carry
    /// meaning (refIds, object/column names), never from document order, GUIDs, or counts.
    /// </summary>
    public required string RuleId { get; init; }

    /// <summary>
    /// One of the categories in <c>ConformanceRulesBuilder</c>, each mapping to a bullet of
    /// Migration-Validation-Plan §3: <c>ControlFlow</c>, <c>Ordering</c>, <c>DataFlow</c>,
    /// <c>SourceColumn</c>, <c>TargetColumn</c>, <c>TargetSchema</c>, <c>Transformation</c>,
    /// <c>SqlStatement</c>, <c>LoadSemantics</c>, <c>ScriptCode</c>.
    /// </summary>
    public required string Category { get; init; }

    public required string PackageName { get; init; }

    /// <summary>Where in the package this obligation comes from -- an executable refId, a component/column path, or a target table name. Same "location is a string, shape varies by category" pattern as <see cref="FindingSpec.Location"/>.</summary>
    public required string Location { get; init; }

    /// <summary>What the replacement has to do, in one sentence, phrased as an obligation ("must ...") rather than a description of the old package.</summary>
    public required string Requirement { get; init; }

    /// <summary>
    /// The concrete fact from the package that produced this rule -- the expression text, the
    /// SQL statement, the declared column type, the truncation disposition. This is what makes
    /// a conformance report reviewable without opening the .dtsx alongside it, and what a
    /// gate-2 test generator would consume next.
    /// </summary>
    public string? Evidence { get; init; }

    /// <summary>
    /// False only for rules that <b>cannot be satisfied by writing code</b> and instead
    /// require a human to read something and sign off -- today just <c>ScriptCode</c>
    /// (Migration-Validation-Plan §13: "Script Tasks remain a manual read"). These still
    /// appear in the report and still must be explicitly claimed; the flag exists so a
    /// conformance percentage isn't quietly inflated by rules no tool could ever verify.
    /// </summary>
    public bool MachineVerifiable { get; init; } = true;
}

/// <summary>
/// The rewrite author's side of the contract: one claim per <see cref="ConformanceRuleSpec.RuleId"/>,
/// hand-maintained (or emitted by the new .NET job's own build) and joined against the
/// generated rules to produce the gate-1 report.
///
/// <b>Why this is a separate file the tool never overwrites:</b> the rules are regenerated
/// from the .dtsx on every run and are therefore disposable; the claims are human work
/// product accumulated over a migration and are not. <c>ssisx conformance</c> writes a stub
/// claim file only when none exists, and refuses to clobber one that does.
/// </summary>
public sealed class ImplementationClaimSpec
{
    public required string RuleId { get; init; }

    /// <summary>
    /// <c>Implemented</c> (the replacement does this), <c>NotApplicable</c> (deliberately not
    /// carried over -- requires <see cref="Note"/>), or <c>Pending</c> (not done yet; the
    /// stub's default for every rule). Anything else is reported as an invalid claim rather
    /// than being treated as one of these, so a typo can't silently read as done.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>Where in the replacement this lives (class/method/file), for the audit trail behind the go-live decision. Free text -- the tool never resolves it.</summary>
    public string? ImplementedBy { get; init; }

    /// <summary>Required for <c>NotApplicable</c>: why this obligation is deliberately dropped. An unexplained NotApplicable is reported as invalid -- that combination is exactly how a real requirement gets quietly deleted.</summary>
    public string? Note { get; init; }
}

/// <summary>The gate-1 result for one package: the plan's own "N rules in spec, M implemented, K unaccounted for" progress metric, plus the detail behind it.</summary>
public sealed class ConformanceReportSpec
{
    public required string PackageName { get; init; }

    public required int TotalRules { get; init; }
    public required int Implemented { get; init; }
    public required int NotApplicable { get; init; }
    public required int Pending { get; init; }

    /// <summary>Rules with no claim at all. Distinct from <see cref="Pending"/> on purpose: a Pending rule is known work, an unclaimed one usually means the rules were regenerated after the package changed and nobody looked -- the more dangerous of the two.</summary>
    public required int Unclaimed { get; init; }

    /// <summary>Claims whose <c>Status</c> is not one of the three legal values, or which are <c>NotApplicable</c> with no <c>Note</c>. Non-zero fails the gate regardless of the other counts.</summary>
    public required int InvalidClaims { get; init; }

    /// <summary>Claims naming a <c>RuleId</c> that no longer exists in the generated rules -- the package changed under the claim file. Surfaced rather than ignored, since it is the signal that a claim needs re-reviewing, not deleting.</summary>
    public required int OrphanedClaims { get; init; }

    /// <summary><see cref="Implemented"/> + <see cref="NotApplicable"/> over <see cref="TotalRules"/>, rounded to 2dp. 100 does not mean "correct" -- only that every obligation is accounted for; correctness is gates 2-4's job.</summary>
    public required double AccountedForPercent { get; init; }
}
