namespace Ssis.Extract.Model.Analysis;

/// <summary>
/// A best-effort primary-key guess for one OLE DB Destination's target table, derived by
/// <c>Ssis.Extract.Dtsx.PrimaryKeyInference</c>. Closes a real gap named in
/// <c>Validation/docs/gate3-harness.md</c>: the gate-3 harness's per-table
/// <c>KeyColumn</c> had to be hand-filled because nothing detected it.
///
/// <b>Why this can only ever be a heuristic, not a fact:</b> a `.dtsx` file carries no
/// primary-key concept anywhere -- confirmed by reading this PoC's own real
/// <c>&lt;externalMetadataColumn&gt;</c> elements, which carry <c>dataType</c>/<c>length</c>/
/// <c>precision</c>/<c>scale</c>/<c>codePage</c> and nothing about keys, nullability, or
/// identity. SSIS simply doesn't persist that at design time. The only signals available
/// without a live database connection (which <c>enrich</c>/<c>pull</c> would have provided,
/// plan §11 decision 4, and which this offline tool deliberately doesn't take) are a naming
/// convention and whether a column is a straight passthrough from a source rather than a
/// computed value -- so every candidate here needs human confirmation, the same "reviewed by
/// a human" framing plan §12 already applies to <c>KeyColumn</c>.
/// </summary>
public sealed class PrimaryKeyCandidateSpec
{
    public required string PackageName { get; init; }
    public required string DataFlowTaskPath { get; init; }
    public required string DestinationComponentName { get; init; }

    /// <summary>The destination component's own refId -- the join key back to a specific component, since <see cref="DestinationComponentName"/> is not guaranteed unique within a Data Flow Task (evidenced on the synthetic Lookup/Conditional Split fixture, which has three OLE DB Destinations all left at the default name "OLE DB Destination").</summary>
    public required string DestinationComponentRefId { get; init; }

    /// <summary>From the destination's own <c>OpenRowset</c>; null when the destination uses a SQL command instead (not modeled here).</summary>
    public string? TargetTable { get; init; }

    /// <summary>Usually one column; empty when <see cref="Confidence"/> is "Unknown".</summary>
    public required List<string> Columns { get; init; }

    /// <summary>"NamingConvention" (the only signal this tool can produce today) or "Unknown" (no candidate found -- reported explicitly rather than omitted, so a human knows to fill it in rather than assuming it was overlooked).</summary>
    public required string Confidence { get; init; }

    /// <summary>Human-readable explanation of what matched (or why nothing did).</summary>
    public required string Reason { get; init; }
}
