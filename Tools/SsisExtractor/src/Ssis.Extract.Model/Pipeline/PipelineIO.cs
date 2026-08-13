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
}

/// <summary>One &lt;output&gt;. <see cref="SynchronousInputId"/> present means this output shares its buffer with that input (a synchronous transform, e.g. Derived Column) -- absent means asynchronous (a blocking/semi-blocking memory consumer, a performance-relevant fact per plan §4.7). Neither of this PoC's components is asynchronous (Sort/Aggregate/Union All etc. are all "(nP)" in the plan), so this field is evidenced as "always present here" but the null case is real schema, not a guess.</summary>
public sealed class PipelineOutputSpec
{
    public required string RefId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool? IsErrorOut { get; init; }
    public string? SynchronousInputId { get; init; }
    public string? ExclusionGroup { get; init; }
    public bool? DeleteOutputOnPathDetached { get; init; }
    public List<PipelineOutputColumnSpec> Columns { get; init; } = [];
    public List<PipelineExternalMetadataColumnSpec> ExternalMetadataColumns { get; init; } = [];
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
