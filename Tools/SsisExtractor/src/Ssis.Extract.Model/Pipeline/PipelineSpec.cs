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

    /// <summary>e.g. "Microsoft.DerivedColumn", "Microsoft.FlatFileSource", "Microsoft.OLEDBDestination". Not normalized against the versioned/GUID forms older packages sometimes use (plan §4.7) -- not evidenced in this PoC, so not guessed at.</summary>
    public required string ComponentClassId { get; init; }

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

/// <summary>Generic name/value/dataType property, used identically for a component's own properties and every input/output column's properties (which is where expressions like Derived Column's live).</summary>
public sealed class PipelinePropertySpec
{
    public required string Name { get; init; }
    public string? DataType { get; init; }
    public string? Description { get; init; }
    public string? Value { get; init; }
}

/// <summary>One &lt;path&gt; -- a physical buffer connection between one component's output and another's input. Note this does NOT fully determine column-level lineage by itself: a synchronous transform's passthrough columns (same buffer, no new output column emitted) are consumed directly by name/lineageId at a downstream component without an intervening path -- see <c>LineageSpec</c>'s doc comment, discovered empirically from this PoC's own <c>LoadEmployees</c> pipeline.</summary>
public sealed class PipelinePathSpec
{
    public required string RefId { get; init; }
    public string? Name { get; init; }
    public required string StartId { get; init; }
    public required string EndId { get; init; }
}
