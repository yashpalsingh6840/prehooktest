namespace Ssis.Extract.Model.Pipeline;

/// <summary>One &lt;input&gt; on a component.</summary>
public sealed class PipelineInputSpec
{
    public required string RefId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? ErrorRowDisposition { get; init; }
    public string? TruncationRowDisposition { get; init; }
    public bool? HasSideEffects { get; init; }
    public List<PipelineInputColumnSpec> Columns { get; init; } = [];
    public List<PipelineExternalMetadataColumnSpec> ExternalMetadataColumns { get; init; } = [];

    /// <summary>Every &lt;property&gt; directly under this input's own &lt;properties&gt; (distinct from any individual input column's own properties). Added alongside <see cref="PipelineOutputSpec.Properties"/> -- see that field's doc comment for why this closes a real coverage gap rather than being speculative.</summary>
    public List<PipelinePropertySpec> Properties { get; init; } = [];
}

/// <summary>One &lt;inputColumn&gt;. <see cref="LineageId"/> is the consuming end of the wire identifier -- it names which upstream output column's value this is, and is the join key <c>LineageBuilder</c> uses to resolve the producer (plan §5.1), independent of whether a &lt;path&gt; directly connects the two components (see <see cref="PipelinePathSpec"/>'s doc comment).</summary>
public sealed class PipelineInputColumnSpec
{
    public required string RefId { get; init; }
    public required string CachedName { get; init; }
    public string? CachedDataType { get; init; }
    public int? CachedLength { get; init; }
    public int? CachedPrecision { get; init; }
    public int? CachedScale { get; init; }
    public int? CachedCodePage { get; init; }
    public required string LineageId { get; init; }
    public string? ExternalMetadataColumnId { get; init; }
    public List<PipelinePropertySpec> Properties { get; init; } = [];

    /// <summary><c>usageType</c> -- <c>readOnly</c>, <c>readWrite</c> or absent. <b>readWrite is
    /// the marker for IN-PLACE COLUMN MODIFICATION</b>, the structural case this whole tool was
    /// blind to before 2026-09-02: a component that rewrites an existing column rather than
    /// adding a new one declares NO output column at all, so every output-column-based safety
    /// net here (computedByName, the unrecognized-producer gate, the expression harvest, the
    /// non-determinism manifest) saw nothing and generated a plain passthrough of the RAW
    /// upstream value. Derived Column's "Replace &lt;column&gt;" mode and Character Map's
    /// in-place mode are both this shape.</summary>
    public string? UsageType { get; init; }

    /// <summary>Raw <c>Expression</c> promoted off this input column's own properties, with
    /// <c>#{lineageId}</c> references intact -- the mirror of
    /// <see cref="PipelineOutputColumnSpec.Expression"/>, but for a Derived Column operating in
    /// "Replace &lt;column&gt;" mode, where the expression is persisted HERE (on the readWrite
    /// input column) and the column keeps its upstream lineageId. Null for an ordinary
    /// read-only input column, which is almost all of them.</summary>
    public string? Expression { get; init; }

    /// <summary>Human-readable form of <see cref="Expression"/> (e.g. <c>UPPER(Name)</c>), same
    /// pairing as <see cref="PipelineOutputColumnSpec.FriendlyExpression"/>.</summary>
    public string? FriendlyExpression { get; init; }

    /// <summary>
    /// For a <c>Microsoft.UnionAll</c>/<c>Microsoft.Merge</c> input column: the LineageId of the
    /// component's OWN output column this input column feeds, promoted off the
    /// <c>OutputColumnLineageID</c> property with its <c>#{...}</c> wrapper stripped.
    ///
    /// <para><b>This is the per-input column mapping, and it is NOT implied by column names.</b>
    /// Measured against real SSIS (2026-09-02): a Union All whose output column is named
    /// <c>UnifiedName</c> accepted input 1 mapping <c>CustName -&gt; UnifiedName</c> and input 2
    /// mapping <c>ClientName -&gt; UnifiedName</c> -- three different names -- and validated
    /// VS_ISVALID. So the long-standing assumption that "real SSIS enforces matching schemas
    /// across every Merge/UnionAll input" is false, and a generated reader keyed on the union's
    /// own output column names cannot be applied blindly to each side's result set.</para>
    /// </summary>
    public string? OutputColumnLineageId { get; init; }
}

/// <summary>One &lt;output&gt;. <see cref="SynchronousInputId"/> present means this output shares its buffer with that input (a synchronous transform, e.g. Derived Column) -- absent means asynchronous (a blocking/semi-blocking memory consumer, a performance-relevant fact per plan §4.7). Neither of this PoC's slice-3 components was asynchronous (Sort/Aggregate/Union All etc. are all "(nP)" in the plan), so this field was evidenced as "always present here" but the null case was always real schema, not a guess -- and the synthetic Lookup/Conditional Split fixtures (component-coverage pass, see docs/report-schema.md) confirm both stay synchronous too.</summary>
public sealed class PipelineOutputSpec
{
    public required string RefId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool? IsErrorOut { get; init; }
    public string? SynchronousInputId { get; init; }
    public string? ExclusionGroup { get; init; }
    public bool? DeleteOutputOnPathDetached { get; init; }

    /// <summary>True when this output has no downstream path attached at all -- a genuine,
    /// real SSIS concept for a component that auto-provisions a fresh "spare" output the moment
    /// an existing one gets connected (confirmed real from Microsoft.Multicast's own saved XML:
    /// a component with N real branches always has N+1 outputs, the last permanently unconnected
    /// and marked <c>dangling="true"</c>). Added 2026-08-28 so PackagePlanner.PlanMulticast can
    /// skip this trailing spare rather than trying (and failing) to resolve it as a real branch.</summary>
    public bool? Dangling { get; init; }

    public List<PipelineOutputColumnSpec> Columns { get; init; } = [];
    public List<PipelineExternalMetadataColumnSpec> ExternalMetadataColumns { get; init; } = [];

    /// <summary>
    /// Every &lt;property&gt; directly under this output's own &lt;properties&gt; -- distinct
    /// from any individual output column's own properties (<see cref="PipelineOutputColumnSpec.Properties"/>).
    /// Added during the synthetic component-coverage pass after discovering Conditional Split
    /// puts its case's whole boolean expression (<c>Expression</c>/<c>FriendlyExpression</c>/
    /// <c>EvaluationOrder</c>, plus <c>IsDefaultOut</c> on the default case) at THIS level, not
    /// on a column -- unlike Derived Column, where the same two property names live per output
    /// COLUMN. Before this field existed, an output-level &lt;properties&gt; block was silently
    /// invisible to the generic model, which matters because DtsxPackageReader.ReadExecutable
    /// deliberately never adds anything under a Data Flow Task's own &lt;pipeline&gt; to
    /// coverage.Unmapped -- the whole subtree is assumed 100% covered on the premise that
    /// PipelineReader structurally models every element generically (see that method's own
    /// comment). Output-level &lt;properties&gt; was a real, silent exception to that premise:
    /// a genuine coverage-percentage overstatement for any package with a Conditional Split
    /// (or any other component using this shape), not just a missing convenience field.
    /// See <see cref="ConditionalSplitPayload"/> for where these are promoted into cases.
    /// </summary>
    public List<PipelinePropertySpec> Properties { get; init; } = [];
}

/// <summary>
/// One &lt;outputColumn&gt; -- where a value is produced. <see cref="LineageId"/> is the
/// producing end of the wire identifier (plan §5.1's join key). <see cref="Expression"/>/
/// <see cref="FriendlyExpression"/> are promoted from this column's own &lt;properties&gt;
/// (also present verbatim in <see cref="Properties"/>) whenever a component happens to
/// carry those two well-known property names -- observed on Derived Column in this PoC,
/// but the promotion itself isn't gated by component type, since nothing else about the
/// schema ties those names to one specific componentClassID.
/// </summary>
public sealed class PipelineOutputColumnSpec
{
    public required string RefId { get; init; }
    public required string Name { get; init; }
    public string? DataType { get; init; }
    public int? Length { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    public int? CodePage { get; init; }
    public required string LineageId { get; init; }
    public string? ExternalMetadataColumnId { get; init; }
    public string? ErrorRowDisposition { get; init; }
    public string? TruncationRowDisposition { get; init; }
    public string? ErrorOrTruncationOperation { get; init; }
    public int? SpecialFlags { get; init; }
    public List<PipelinePropertySpec> Properties { get; init; } = [];

    /// <summary>Raw form, with <c>#{lineageId}</c> references to upstream columns -- e.g. <c>(DT_WSTR,101)(#{...Columns[FirstName]} + " " + #{...Columns[LastName]})</c>. Null when this column has no Expression property (e.g. a plain source/destination column).</summary>
    public string? Expression { get; init; }

    /// <summary>Human-readable form of <see cref="Expression"/>, e.g. <c>(DT_WSTR,101)(FirstName + " " + LastName)</c>. Kept alongside the raw form per plan §4.7: friendly for humans/test generation, raw for exact lineage resolution.</summary>
    public string? FriendlyExpression { get; init; }
}

/// <summary>The design-time external-schema contract (plan §4.7) -- comparing this against the live database/file schema is a cheap drift check, not yet automated here (later slice).</summary>
public sealed class PipelineExternalMetadataColumnSpec
{
    public required string RefId { get; init; }
    public required string Name { get; init; }
    public string? DataType { get; init; }
    public int? Length { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    public int? CodePage { get; init; }
}
