namespace Ssis.Extract.Model.Pipeline;

/// <summary>
/// The full contents of one <c>Microsoft.Pipeline</c> task's <c>&lt;pipeline&gt;</c>
/// element (plan §4.7). Slice 2 only kept this subtree as raw XML
/// (<c>DataFlowTaskMarker.RawPipelineXml</c>); slice 3 parses it completely, generically,
/// for every component regardless of type -- see <see cref="PipelineComponentSpec"/> for
/// where component-specific semantic extraction is layered on top for the types this PoC
/// has real evidence for.
/// </summary>
public sealed class PipelineSpec
{
    public string? Version { get; init; }
    public List<PipelineComponentSpec> Components { get; init; } = [];
    public List<PipelinePathSpec> Paths { get; init; } = [];
}

/// <summary>
/// One <c>&lt;component&gt;</c>. The generic model (<see cref="Properties"/>,
/// <see cref="Connections"/>, <see cref="Inputs"/>, <see cref="Outputs"/>) is populated for
/// every component regardless of <see cref="ComponentClassId"/> -- this is what makes an
/// unrecognized/third-party component type safe: nothing is lost, there's just no bespoke
/// semantic payload below layered on top of it (plan §4.7's mandatory "anything else" row,
/// satisfied here without a separate raw-XML fallback because the generic model already
/// captures every property/column verbatim as data, not text).
/// </summary>
public sealed class PipelineComponentSpec
{
    public required string RefId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>e.g. "Microsoft.DerivedColumn", "Microsoft.FlatFileSource", "Microsoft.OLEDBDestination". Always canonical -- a legacy-spelled SSIS 2005/2008-era ProgID (<c>DTSAdapter.X.N</c>/<c>DTSTransform.X.N</c>) or a known bare CLSID is normalized to this form by <c>LegacyComponentIds</c> before this spec is built; see <see cref="RawComponentClassId"/> for what was actually in the file.</summary>
    public required string ComponentClassId { get; init; }

    /// <summary>Non-null only when <see cref="ComponentClassId"/> was normalized from a legacy spelling -- the verbatim value read from <c>componentClassID</c> before normalization, kept so extraction stays lossless. Null (not merely equal to <see cref="ComponentClassId"/>) whenever the file already used the canonical form -- the common case, kept null to avoid bloating every golden file with a redundant field.</summary>
    public string? RawComponentClassId { get; init; }

    /// <summary>True when the raw <c>componentClassID</c> is shaped like a bare CLSID (<c>{8-4-4-4-12}</c>) that <c>LegacyComponentIds</c> has no evidenced mapping for -- the honest "we genuinely don't know what this is" case, as opposed to a recognized legacy spelling (silently normalized) or an ordinary named third-party component (its own, separate <c>third-party-component</c> finding). See <c>RulesEngine</c>'s <c>unmapped-legacy-clsid</c> finding.</summary>
    public bool IsUnresolvedLegacyClsid { get; init; }

    public string? ContactInfo { get; init; }
    public string? Version { get; init; }
    public string? LocaleId { get; init; }
    public bool? ValidateExternalMetadata { get; init; }
    public bool? UsesDispositions { get; init; }

    /// <summary>Every &lt;property&gt; under the component's own &lt;properties&gt; -- generic name/value/dataType capture, works for any component including unrecognized ones.</summary>
    public List<PipelinePropertySpec> Properties { get; init; } = [];

    public List<PipelineComponentConnectionSpec> Connections { get; init; } = [];
    public List<PipelineInputSpec> Inputs { get; init; } = [];
    public List<PipelineOutputSpec> Outputs { get; init; } = [];

    // Component-specific semantic extraction (plan §4.7's per-component table), populated
    // only for componentClassID values this build slice has read a real example of -- both
    // present in this PoC. Derived Column needed no dedicated payload type: its value-add
    // (Expression/FriendlyExpression) is promoted generically onto every output column
    // (see PipelineOutputColumnSpec), not gated by component type, since any component
    // could in principle carry those same two well-known property names. Add a new payload
    // type here only after reading a real example (same rule CLAUDE.md trap 12 established
    // for the type-code tables) -- do not extrapolate from documentation alone.
    public FlatFileSourcePayload? FlatFileSource { get; init; }
    public OleDbDestinationPayload? OleDbDestination { get; init; }

    /// <summary>Added 2026-08-28 testing this tool against a real third-party portfolio
    /// (SSIS_From_Sandeep) -- the write-side mirror of <see cref="FlatFileSourcePayload"/>, see
    /// <see cref="FlatFileDestinationPayload"/>'s own doc comment.</summary>
    public FlatFileDestinationPayload? FlatFileDestination { get; init; }

    // Added during the synthetic component-coverage pass (docs/report-schema.md) once real
    // evidence existed for these three types too -- same "read a real example first" rule.
    public OleDbSourcePayload? OleDbSource { get; init; }
    public LookupPayload? Lookup { get; init; }
    public ConditionalSplitPayload? ConditionalSplit { get; init; }

    /// <summary>Added 2026-08-27 testing this tool against a real third-party portfolio
    /// (SSIS_From_Sandeep) -- same "read a real example first" rule, discriminated by
    /// UserComponentTypeName, not ComponentClassId; see AdoNetDestinationPayload/
    /// AdoNetSourcePayload's own doc comments for why.</summary>
    public AdoNetDestinationPayload? AdoNetDestination { get; init; }
    public AdoNetSourcePayload? AdoNetSource { get; init; }

    /// <summary>Discriminated by the <c>UserComponentTypeName</c> custom property, not <see cref="ComponentClassId"/> -- see <see cref="Ssis.Extract.Model.Pipeline.ScriptComponentPayload"/>'s own doc comment for why.</summary>
    public ScriptComponentPayload? ScriptComponent { get; init; }

    /// <summary>Added 2026-08-28 testing this tool against a real third-party portfolio
    /// (SSIS_From_Sandeep) -- <c>Microsoft.DataConvert</c>, see <see cref="DataConvertPayload"/>'s
    /// own doc comment.</summary>
    public DataConvertPayload? DataConvert { get; init; }

    /// <summary>Added 2026-08-28 testing this tool against a real third-party portfolio
    /// (SSIS_From_Sandeep) -- <c>Microsoft.Sort</c>/<c>Microsoft.MergeJoin</c>, see their own
    /// doc comments.</summary>
    public SortPayload? Sort { get; init; }
    public MergeJoinPayload? MergeJoin { get; init; }

    /// <summary>Added 2026-08-28 testing this tool against a real third-party portfolio
    /// (SSIS_From_Sandeep) -- <c>Microsoft.ExcelSource</c>, see <see cref="ExcelSourcePayload"/>'s
    /// own doc comment.</summary>
    public ExcelSourcePayload? ExcelSource { get; init; }

    /// <summary>Added 2026-08-28 testing this tool against a real third-party portfolio
    /// (SSIS_From_Sandeep) -- <c>Microsoft.OLEDBCommand</c>, see <see cref="OleDbCommandPayload"/>'s
    /// own doc comment.</summary>
    public OleDbCommandPayload? OleDbCommand { get; init; }

    /// <summary>Added 2026-08-30, built speculatively (RBC_Demo_ETL's own real instance,
    /// <c>DFT_LookupAndAggregate\AGG_ByRegion</c>, sits downstream of a Lookup that already
    /// blocks the whole flow regardless of Aggregate support) -- <c>Microsoft.Aggregate</c>, see
    /// <see cref="AggregatePayload"/>'s own doc comment.</summary>
    public AggregatePayload? Aggregate { get; init; }

    /// <summary><c>Microsoft.RowCount</c>, see <see cref="RowCountPayload"/>'s own doc comment.</summary>
    public RowCountPayload? RowCount { get; init; }
}

/// <summary>A component's reference to a connection manager. <see cref="ConnectionManagerRefRaw"/> is the pipeline's own reference shape (a refId path, e.g. "Package.ConnectionManagers[CM_EmployeesCsv]") -- notably NOT the DTSID-GUID form Execute SQL Task uses (see <c>ExecuteSqlTaskPayload.ConnectionRefRaw</c>'s doc comment); resolved the same way, by matching against <c>ConnectionManagerSpec.RefId</c> this time instead of <c>DtsId</c>.</summary>
public sealed class PipelineComponentConnectionSpec
{
    public required string RefId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? ConnectionManagerRefRaw { get; init; }
    public string? ConnectionManagerName { get; init; }
}

/// <summary>
/// Generic name/value/dataType property, used identically for a component's own properties
/// and every input/output column's properties (which is where expressions like Derived
/// Column's live). <see cref="IsArray"/> covers a real, previously-lossy gap: an
/// <c>isArray="true"</c> property (confirmed real via Script Component's <c>SourceCode</c>/
/// <c>BinaryCode</c>/<c>BreakpointCollection</c> custom properties, verified end-to-end
/// through the object model's own property-array serialization, not guessed) persists as
/// <c>&lt;arrayElements arrayElementCount="N"&gt;&lt;arrayElement&gt;text&lt;/arrayElement&gt;...</c>,
/// which <see cref="Value"/> alone (backed by <c>XElement.Value</c>) would silently
/// concatenate into one run-on string with every element boundary lost. When
/// <see cref="IsArray"/> is true, read <see cref="ArrayElements"/> instead -- <see cref="Value"/>
/// is not populated for an array property.
/// </summary>
public sealed class PipelinePropertySpec
{
    public required string Name { get; init; }
    public string? DataType { get; init; }
    public string? Description { get; init; }
    public string? Value { get; init; }

    public bool IsArray { get; init; }

    /// <summary>One entry per &lt;arrayElement&gt;, in document order, only populated when <see cref="IsArray"/> is true.</summary>
    public List<string> ArrayElements { get; init; } = [];
}

/// <summary>One &lt;path&gt; -- a physical buffer connection between one component's output and another's input. Note this does NOT fully determine column-level lineage by itself: a synchronous transform's passthrough columns (same buffer, no new output column emitted) are consumed directly by name/lineageId at a downstream component without an intervening path -- see <c>LineageSpec</c>'s doc comment, discovered empirically from this PoC's own <c>LoadEmployees</c> pipeline.</summary>
public sealed class PipelinePathSpec
{
    public required string RefId { get; init; }
    public string? Name { get; init; }
    public required string StartId { get; init; }
    public required string EndId { get; init; }
}
