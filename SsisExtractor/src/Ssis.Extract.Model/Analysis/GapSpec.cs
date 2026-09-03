using System.Text.Json.Serialization;

namespace Ssis.Extract.Model.Analysis;

/// <summary>
/// What KIND of thing <c>ssisx generate</c> could not produce -- the discriminator that decides
/// whether a gap is something a human working with AI can close at all, and if so how.
///
/// <b>Why this exists as a separate axis from <c>GenerationGap.IsBlocking</c>:</b> that flag
/// answers "does this stop the package being generatable" (a reporting concern). This answers
/// "who can close it, and with what" (a workflow concern). A Lookup join key and an unsupported
/// component type are both blocking, but one is a single missing FACT a reviewer can confirm in
/// seconds and the other is missing tool support that must be built deterministically -- routing
/// them the same way is the central mistake this whole design exists to avoid.
///
/// Only the kinds that map to <see cref="GapTier.MissingDatum"/>/<see cref="GapTier.MissingLogic"/>
/// are ever set explicitly; everything else stays <see cref="Unclassified"/> and derives its tier
/// from <c>IsBlocking</c>. That keeps the classification change to a handful of call sites rather
/// than all ~143 <c>new GenerationGap(...)</c> sites in this assembly.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GapKind
{
    /// <summary>No specific workflow attached: either an advisory (when the gap is non-blocking) or missing tool support (when it is blocking). The default for every unclassified call site.</summary>
    Unclassified = 0,

    /// <summary>A <c>Microsoft.ScriptTask</c> the planner cannot translate. Its real source IS present in the .dtsx (<c>ScriptTaskPayload.ProjectItems</c>), so this is a translation job, not a missing-information job.</summary>
    ScriptTask,

    /// <summary>A destination column synthesized by a Script Component (<c>ScriptComponentPayload.SourceCodeItems</c>). Same category as <see cref="ScriptTask"/> -- real source, no mechanical translation.</summary>
    ScriptComponentColumn,

    /// <summary>A <c>Microsoft.Lookup</c>'s join key, which SSIS genuinely does not persist anywhere in the saved .dtsx. One missing DATUM; every line of code around it is derivable once it is supplied.</summary>
    LookupJoinKey,

    /// <summary>A conditional precedence constraint (<c>DTS:Value</c> and/or <c>DTS:EvalOp</c>) in a form
    /// this tool does not generate. Tier 3 by derivation, deliberately: the outcome-based forms need a
    /// failure-handler position OUTSIDE the generated package transaction, which is one emitter feature
    /// -- not something a human should hand-patch per package.</summary>
    ConditionalConstraint,

    /// <summary>
    /// A connection manager's sensitive property (e.g. a Password) is DPAPI-encrypted in the saved
    /// .dtsx (ProtectionLevel EncryptSensitiveWithUserKey/EncryptSensitiveWithPassword) and therefore
    /// undecryptable outside the original author's Windows account. One missing DATUM -- the real
    /// value -- everything else (the User Secrets slot itself) is already wired unconditionally. Unlike
    /// <see cref="LookupJoinKey"/>, the "decision" here is an ACKNOWLEDGMENT, never a value: a secret
    /// must never be written into a hand-maintained, tracked <c>decisions.json</c> file.
    /// </summary>
    EncryptedConnectionManagerSecret,
}

/// <summary>
/// How a <see cref="GapKind"/> should be worked, derived (never stored twice) by
/// <c>GapIdentity.TierOf</c>. See the plan's own taxonomy table -- the tiers are the whole point
/// of classifying gaps at all.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GapTier
{
    /// <summary>Generation genuinely produced complete, wired output; this is something to read before running it. Never has a work packet.</summary>
    Advisory,

    /// <summary>Tier 1 -- information absent from the .dtsx, but the code is fully derivable once it is supplied. Closed by confirming a fact, NOT by anyone writing code: the deterministic emitter still generates everything.</summary>
    MissingDatum,

    /// <summary>Tier 2 -- the real logic exists in the .dtsx as script source, just not mechanically translatable. Closed by a reviewed, provenance-stamped translation into a defined seam.</summary>
    MissingLogic,

    /// <summary>Tier 3 -- this tool cannot model the shape at all. Deliberately NOT closeable by an AI fill: patching it per-package would hide one systemic gap behind N one-off patches. Belongs in the emitter, with a fixture and a test.</summary>
    MissingToolSupport,
}

/// <summary>
/// One row of <c>gaps.json</c>: the machine-readable index of everything <c>ssisx generate</c>
/// could not produce for one package, keyed by a stable <see cref="GapId"/>.
///
/// <b><see cref="GapId"/> stability is a hard requirement, not tidiness</b> -- exactly as for
/// <see cref="ConformanceRuleSpec.RuleId"/>, which this deliberately mirrors. Fill files and
/// decision entries are keyed by these ids and are hand-maintained across a migration, so an id
/// that shifted when someone nudged a package in the designer would orphan the work behind it.
/// Ids therefore derive only from meaning-bearing identifiers (package/object/column names),
/// never from document order, GUIDs, or counts.
/// </summary>
public sealed class GapSpec
{
    /// <summary>Stable, human-readable id of the form <c>&lt;KIND&gt;:&lt;Package&gt;:&lt;Location&gt;</c>, e.g. <c>SCRIPT-COLUMN:Package:StagingCustomers.FullName</c>.</summary>
    public required string GapId { get; init; }

    public required string Package { get; init; }
    public required GapKind Kind { get; init; }
    public required GapTier Tier { get; init; }

    /// <summary>Same shape and meaning as <c>GenerationGap.Location</c> -- an entity.column path, a task name, or a component name.</summary>
    public required string Location { get; init; }

    public required string Reason { get; init; }
    public required bool IsBlocking { get; init; }

    /// <summary>Relative path of the work packet written for this gap, or null when its tier has none (Advisory/MissingToolSupport).</summary>
    public string? PacketPath { get; init; }

    /// <summary>
    /// SHA-256 over exactly the evidence text embedded in this gap's own work packet -- the
    /// script source, the reference SQL, the column lists. Recorded from the first run so that a
    /// later fill can be checked against the evidence it was actually written for: if the .dtsx
    /// changes underneath it, this hash moves and the fill is stale rather than silently reused.
    /// Null when the gap has no packet.
    /// </summary>
    public string? EvidenceSha256 { get; init; }

    /// <summary>
    /// The RefId of the executable/component this gap's evidence came from -- the exact same value
    /// <c>ConformanceRulesBuilder.ScriptCodeRules</c> uses to build a <c>ScriptCode</c> rule's own
    /// <c>RuleId</c> (<c>ConformanceRulesBuilder.ScriptCodeRuleId</c>). Persisted so a consumer of
    /// <c>gaps.json</c> alone (like <c>ApplyFillsCommand</c>, which never has the live
    /// <c>PackageSpec</c>/<c>GenerationGap</c> objects this value already lived on in memory) can
    /// compute that RuleId directly and link an applied Tier-2 fill to the gate-1 conformance
    /// obligation it answers, without re-deriving or guessing the identifier. Null for a gap kind
    /// with no evidence-bearing single object (e.g. an unclassified gap).
    /// </summary>
    public string? EvidenceRefId { get; init; }
}
