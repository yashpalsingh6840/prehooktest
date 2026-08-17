namespace Ssis.Extract.Model.Pipeline;

/// <summary>One design-time column ↔ external (file/table) column mapping, derived from an input/output column's <c>externalMetadataColumnId</c> resolved against the same input/output's own <c>externalMetadataColumns</c> list -- not a separate mapping section in the source XML.</summary>
public sealed class PipelineColumnMappingSpec
{
    public required string ComponentColumnName { get; init; }
    public required string ExternalColumnName { get; init; }
    public string? ExternalDataType { get; init; }
}

/// <summary>
/// <c>Microsoft.FlatFileSource</c> (plan §4.7 table: "connection + column mapping,
/// Overwrite, header"). <see cref="ColumnMappings"/> is built from the main (non-error)
/// output's columns against their <c>externalMetadataColumns</c> -- the design-time
/// contract with the flat file's own schema.
/// </summary>
public sealed class FlatFileSourcePayload
{
    public string? ConnectionName { get; init; }

    /// <summary>Whether zero-length columns are treated as null.</summary>
    public bool? RetainNulls { get; init; }

    /// <summary>Name of an output column carrying the source file name, if configured. Empty/null means no such column is generated.</summary>
    public string? FileNameColumnName { get; init; }

    public List<PipelineColumnMappingSpec> ColumnMappings { get; init; } = [];
}

/// <summary>
/// <c>Microsoft.OLEDBDestination</c> (plan §4.7 table). <see cref="AccessMode"/> is kept as
/// the raw integer only -- the XML tags it <c>typeConverter="AccessMode"</c> but doesn't
/// spell out the enum's text values in this file, and CLAUDE.md trap 12's lesson is to not
/// hand-guess an enum without runtime/documentation evidence; a future slice can resolve it
/// via the object-model oracle (plan §6, slice 6) if needed.
/// </summary>
public sealed class OleDbDestinationPayload
{
    public string? ConnectionName { get; init; }

    /// <summary>The target table/view, e.g. "[dbo].[Employee]". Null/empty when <see cref="SqlCommand"/> is used instead (AccessMode = SQL command).</summary>
    public string? OpenRowset { get; init; }

    public string? SqlCommand { get; init; }
    public int? AccessMode { get; init; }
    public string? FastLoadOptions { get; init; }
    public bool? FastLoadKeepIdentity { get; init; }
    public bool? FastLoadKeepNulls { get; init; }
    public int? FastLoadMaxInsertCommitSize { get; init; }
    public int? CommandTimeout { get; init; }

    /// <summary>Input columns ↔ external (target table) columns, built from the main input's columns against their <c>externalMetadataColumns</c>.</summary>
    public List<PipelineColumnMappingSpec> ColumnMappings { get; init; } = [];
}

/// <summary>
/// <c>Microsoft.OLEDBSource</c> -- the read-side mirror of <see cref="OleDbDestinationPayload"/>,
/// added during the synthetic component-coverage pass (docs/report-schema.md) alongside
/// Lookup/Conditional Split since it's the natural upstream component for both and this PoC
/// had no prior OLE DB Source evidence (both real PoC packages read from flat files only).
/// Same <see cref="AccessMode"/> raw-integer caveat as the destination payload -- confirmed
/// value <c>2</c> = SQL command against the synthetic fixture, not otherwise decoded.
/// </summary>
public sealed class OleDbSourcePayload
{
    public string? ConnectionName { get; init; }

    /// <summary>The source table/view. Null/empty when <see cref="SqlCommand"/> is used instead.</summary>
    public string? OpenRowset { get; init; }

    public string? SqlCommand { get; init; }
    public int? AccessMode { get; init; }
    public int? CommandTimeout { get; init; }

    /// <summary>Output columns ↔ external (source table) columns, built from the main (non-error) output's columns against their <c>externalMetadataColumns</c> -- same construction as <see cref="FlatFileSourcePayload.ColumnMappings"/>.</summary>
    public List<PipelineColumnMappingSpec> ColumnMappings { get; init; } = [];
}

/// <summary>
/// <c>Microsoft.Lookup</c> (plan §5.7 names it explicitly as a common gap component).
/// Confirmed by building a real Lookup via the SSIS 17 object model against a live
/// SqlServer reference table (docs/report-schema.md "Synthetic component-coverage
/// fixtures") -- <see cref="MatchOutputName"/>/<see cref="NoMatchOutputName"/> are SSIS's
/// own fixed output names for this component (not user-renamed the way a Conditional
/// Split case is), confirmed against that fixture's own saved XML.
/// <see cref="ReferenceColumns"/> is a best-effort parse of the component's own
/// <c>ReferenceMetadataXml</c> custom property -- an internal, undocumented nested-XML-
/// in-a-string format (not the standard <c>externalMetadataColumns</c> mechanism
/// OLEDBDestination/OLEDBSource use, confirmed empirically: this collection stayed empty
/// on the synthetic fixture even after the join key was set and the component fully
/// reinitialized). Parsed defensively -- null on any shape this format doesn't match,
/// rather than a hard failure, since nothing here documents that this internal shape is
/// stable across SSIS versions.
/// </summary>
public sealed class LookupPayload
{
    public string? ConnectionName { get; init; }
    public string? SqlCommand { get; init; }

    /// <summary>Raw <c>NoMatchBehavior</c> integer -- 1 = redirect no-match rows to <see cref="NoMatchOutputName"/> on the synthetic fixture; not otherwise decoded (same raw-enum caveat as OleDbDestinationPayload.AccessMode).</summary>
    public int? NoMatchBehaviorRaw { get; init; }

    /// <summary>Raw <c>CacheType</c> integer (0 = Full cache on the synthetic fixture's default).</summary>
    public int? CacheTypeRaw { get; init; }

    public string? MatchOutputName { get; init; }
    public string? NoMatchOutputName { get; init; }

    public List<LookupReferenceColumnSpec> ReferenceColumns { get; init; } = [];
}

/// <summary>One <c>&lt;referenceColumn&gt;</c> parsed out of a Lookup's <c>ReferenceMetadataXml</c> custom property -- see <see cref="LookupPayload.ReferenceColumns"/>.</summary>
public sealed class LookupReferenceColumnSpec
{
    public required string Name { get; init; }
    public string? DataType { get; init; }
    public int? Length { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
}

/// <summary>
/// <c>Microsoft.ConditionalSplit</c>. Built from each non-error/non-default output's own
/// <see cref="PipelineOutputSpec.Properties"/> (see that field's doc comment for the
/// coverage gap discovering this closed) -- confirmed against a real Conditional Split
/// built via the object model, one case output plus the always-present default and error
/// outputs. <see cref="Cases"/> is ordered by <see cref="ConditionalSplitCaseSpec.EvaluationOrder"/>
/// where present, matching the order SSIS itself evaluates them in at runtime (first match
/// wins) -- not just document order.
/// </summary>
public sealed class ConditionalSplitPayload
{
    public string? DefaultOutputName { get; init; }
    public List<ConditionalSplitCaseSpec> Cases { get; init; } = [];
}

/// <summary>One non-default, non-error output of a Conditional Split -- one evaluated case.</summary>
public sealed class ConditionalSplitCaseSpec
{
    public required string OutputName { get; init; }

    /// <summary>Raw form, with <c>#{lineageId}</c> references -- same two-form pattern as <see cref="PipelineOutputColumnSpec.Expression"/>/<see cref="PipelineOutputColumnSpec.FriendlyExpression"/>, just promoted from the output level instead of a column.</summary>
    public string? Expression { get; init; }
    public string? FriendlyExpression { get; init; }
    public int? EvaluationOrder { get; init; }
}
