using System.Text.Json.Serialization;

namespace Ssis.Extract.Model.Analysis;

/// <summary>
/// What happened to one seam (a Script Component's own combined method, named after the
/// component, or one Script Task's <c>RunScriptAsync</c>) found inside a Tier-2 fill file,
/// recorded in <c>fills-applied.json</c> by <c>ssisx apply-fills</c>.
///
/// Mirrors <see cref="GapDecisionStatus"/>'s own shape and reasoning, one tier over: a fill that
/// LOOKS applied but was not checked against the evidence it was written for is exactly the
/// failure this whole gap-taxonomy design exists to prevent.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FillStatus
{
    /// <summary>This SEAM's own recorded evidence hash matches the package's current one -- a fact
    /// about the seam, not a guarantee the file landed: a sibling seam in the SAME file being
    /// <see cref="Stale"/> blocks the whole file from being copied (there is no safe way to strip
    /// just one method out of an otherwise-fine file), in which case this seam is still, in effect,
    /// unfilled even though its own translation is current. Check <c>generate/&lt;Package&gt;/Fills/</c>
    /// for whether the file itself was actually copied.</summary>
    Applied,

    /// <summary>Copied, but the fill carries no <c>// ssisx-fill:</c> provenance comment at all --
    /// every fill written before this feature existed, or one a human skipped. Not a refusal: with
    /// no recorded hash there is nothing to compare, so it is trusted as written, exactly as before
    /// this feature existed. Reported so the practice of adding one visibly compounds over time.</summary>
    Unattributed,

    /// <summary>The fill's own recorded <c>EvidenceSha256</c> no longer matches the package's
    /// current one -- NOT copied. The .dtsx changed underneath this translation; the fix is to
    /// re-read the current work packet and re-port, not to trust the old translation.</summary>
    Stale,

    /// <summary>Implements a seam no gap asked for -- NOT copied. Same meaning as before this
    /// feature existed (the package changed and the seam disappeared); recorded here too so one
    /// manifest carries the complete audit trail instead of splitting it across a console log.</summary>
    Orphaned,
}

/// <summary>
/// One row of <c>fills-applied.json</c> -- <c>ssisx apply-fills</c>' own audit trail of every seam
/// it found in <c>fills/&lt;Package&gt;/*.cs</c>, what it decided about it, and why. Written fresh
/// on every run (this file is generated output, not hand-maintained -- the hand-maintained side is
/// the fill source itself, under <c>fills/</c>, which this command only ever reads).
/// </summary>
public sealed class FillRecordSpec
{
    public required string Package { get; init; }
    public required string FileName { get; init; }

    /// <summary>The seam this record is about: a component's own combined method name
    /// (<c>SCR_CleanseCustomerRow</c>) or, for a Script Task, <c>&lt;ClassName&gt;.RunScriptAsync</c>
    /// -- the same qualifier <c>ApplyFillsCommand</c>'s own console report already uses.</summary>
    public required string Seam { get; init; }

    /// <summary>Null only for <see cref="FillStatus.Orphaned"/> -- there is no gap to point at.</summary>
    public string? GapId { get; init; }

    public required FillStatus Status { get; init; }

    /// <summary>From the fill's own <c>// ssisx-fill:</c> comment, when present.</summary>
    public string? Author { get; init; }

    public string? Date { get; init; }

    /// <summary>The evidence hash this fill was written against, from its own provenance comment.
    /// Null when the fill carries none (<see cref="FillStatus.Unattributed"/>) or answers no real
    /// gap (<see cref="FillStatus.Orphaned"/>).</summary>
    public string? RecordedEvidenceSha256 { get; init; }

    /// <summary>The gap's evidence hash as of THIS run, from <c>gaps.json</c> -- shown alongside
    /// <see cref="RecordedEvidenceSha256"/> so a stale record is self-explanatory without anyone
    /// re-deriving it from two separate files.</summary>
    public string? CurrentEvidenceSha256 { get; init; }

    /// <summary>
    /// The gate-1 <c>ScriptCode</c> conformance <c>RuleId</c> this seam's own translation
    /// contributes to, when its gap kind carries one (<c>ScriptTask</c>/<c>ScriptComponentColumn</c>,
    /// via <c>GapSpec.EvidenceRefId</c> and <c>ConformanceRulesBuilder.ScriptCodeRuleId</c>). A
    /// Script Component's combined seam already answers every column it produces in one method, so
    /// this value is set the moment that ONE seam is filled -- there is no partial-component case to
    /// reconcile any more. Null for every other gap kind, and for an
    /// <see cref="FillStatus.Orphaned"/> seam (no gap, so no rule to point at).
    /// </summary>
    public string? ConformanceRuleId { get; init; }
}
