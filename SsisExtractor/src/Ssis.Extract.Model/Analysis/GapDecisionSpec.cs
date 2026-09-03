namespace Ssis.Extract.Model.Analysis;

/// <summary>
/// One confirmed answer to a <see cref="GapTier.MissingDatum"/> (Tier 1) work packet: a FACT the
/// <c>.dtsx</c> does not carry, supplied from outside so the deterministic emitter can generate
/// the code around it. Read from <c>fills/&lt;Package&gt;.decisions.json</c>, a
/// dictionary keyed by <see cref="GapSpec.GapId"/>.
///
/// <b>This file is hand-maintained work product and <c>ssisx</c> never writes it</b> -- exactly
/// the lifecycle <c>ConformanceCommand</c> already established for implementation claims, and for
/// the same reason: generated output is disposable and regenerated on every run, while the record
/// of which decisions were made and why is accumulated across a migration and must survive
/// regeneration.
///
/// <b><see cref="ConfirmedBy"/> is what makes a decision usable, not <see cref="Confidence"/>.</b>
/// An AI proposing a join key is a proposal; a human putting their name to it is the decision. An
/// entry with no <see cref="ConfirmedBy"/> is reported as pending and NOT used, however confident
/// it claims to be -- which is the whole point of a human in this loop rather than a silent guess.
/// </summary>
public sealed class GapDecisionSpec
{
    // NOTE: an entry for GapKind.EncryptedConnectionManagerSecret uses only ConfirmedBy/ConfirmedOn/
    // Rationale/EvidenceSha256 below -- InputColumn/ReferenceColumn are Lookup-specific. There is
    // deliberately no "Value"/"Secret" field anywhere on this type: an acknowledgment for that gap
    // kind records that a human is aware and has supplied the real value elsewhere (User Secrets),
    // never the value itself -- this file is tracked and hand-maintained, and a secret must never
    // live in it.

    /// <summary>For a Lookup join key: the pipeline INPUT column the component joins on.</summary>
    public string? InputColumn { get; init; }

    /// <summary>For a Lookup join key: the column in the reference query's own result set that <see cref="InputColumn"/> matches against.</summary>
    public string? ReferenceColumn { get; init; }

    /// <summary>The proposer's own stated confidence (High/Medium/Low). Recorded for the audit trail; deliberately NOT a gate -- see <see cref="ConfirmedBy"/>.</summary>
    public string? Confidence { get; init; }

    public string? Rationale { get; init; }

    /// <summary>Who confirmed this. Required: an entry without it is a proposal, not a decision, and is never used.</summary>
    public string? ConfirmedBy { get; init; }

    public string? ConfirmedOn { get; init; }

    /// <summary>
    /// The <see cref="GapSpec.EvidenceSha256"/> this decision was made against. When present and
    /// no longer matching, the package changed underneath the decision and it is reported stale
    /// rather than silently reused -- the same distinction the conformance design draws between
    /// an unclaimed rule and a pending one. Optional today; chunk 4 makes recording it routine.
    /// </summary>
    public string? EvidenceSha256 { get; init; }
}

/// <summary>Why a decision present in the file was not used, or that it was. Reported rather than
/// silently applied/ignored -- a decision that looks applied but was not is exactly the failure
/// this tool exists to prevent.</summary>
public enum GapDecisionStatus
{
    /// <summary>Confirmed, evidence matches, applied to generation.</summary>
    Applied,

    /// <summary>No <see cref="GapDecisionSpec.ConfirmedBy"/>: a proposal awaiting human sign-off. Not used.</summary>
    Unconfirmed,

    /// <summary>The evidence hash no longer matches the package. Not used, must be re-reviewed.</summary>
    Stale,

    /// <summary>Names a GapId that no longer exists -- the package changed underneath it.</summary>
    Orphaned,

    /// <summary>Confirmed, but missing a field this gap kind requires (e.g. a Lookup decision with no InputColumn).</summary>
    Incomplete,
}
