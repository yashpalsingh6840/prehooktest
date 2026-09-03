namespace Ssis.Extract.Model.Pipeline;

/// <summary>
/// Column-level lineage for one Data Flow Task, derived from a <see cref="PipelineSpec"/>
/// by <c>Ssis.Extract.Dtsx.LineageBuilder</c> (plan §5.1) -- not read from the XML directly.
///
/// <b>Key discovery, evidenced from this PoC's own <c>LoadEmployees</c> pipeline:</b> a
/// &lt;path&gt; is a physical buffer connection, not the thing that determines column
/// lineage. A synchronous transform's passthrough columns (e.g. Derived Column's
/// <c>EmployeeID</c>/<c>Department</c> inputs, never re-emitted as its own output columns)
/// are consumed downstream by matching <c>lineageId</c> directly against the *original*
/// producing component, skipping the intermediate component's own output entirely -- there
/// is no &lt;path&gt; from the flat file source straight to the OLE DB destination for those
/// columns, yet that is exactly where their value comes from. So <see cref="Edges"/> is
/// built by matching <c>lineageId</c> globally across every inputColumn/outputColumn in the
/// pipeline, never by walking <see cref="PipelineSpec.Paths"/> -- which turns out to also
/// match the plan's own §5.1 example diagram (direct arrows bypassing the Derived Column
/// subgraph for passthrough columns), not a simplification invented here.
/// </summary>
public sealed class LineageSpec
{
    public List<LineageEdgeSpec> Edges { get; init; } = [];

    /// <summary>Output columns whose Expression contains zero <c>#{lineageId}</c> references -- i.e. a literal/constant/non-deterministic-function value with no upstream source (e.g. <c>GETUTCDATE()</c>). These deliberately get no incoming edge in <see cref="Edges"/> rather than a fabricated one.</summary>
    public List<LineageColumnRefSpec> ConstantColumns { get; init; } = [];

    /// <summary>Output columns whose <c>lineageId</c> is never referenced by any inputColumn or any other output column's Expression anywhere in this pipeline -- produced but never consumed. Covers any component's output, not only true sources (distinguishing "source-kind" components from transforms would mean guessing a componentClassID convention beyond what this PoC's fixtures evidence).</summary>
    public List<LineageColumnRefSpec> UnusedColumns { get; init; } = [];
}

/// <summary>One column-to-column lineage hop.</summary>
public sealed class LineageEdgeSpec
{
    public required string FromComponentRefId { get; init; }
    public required string FromComponentName { get; init; }
    public required string FromColumnRefId { get; init; }
    public required string FromColumnName { get; init; }

    /// <summary>The shared wire identifier joining producer and consumer -- see this type's doc comment. For a <c>PathFlow</c> edge this is the same value at both ends by definition; kept as one field rather than duplicated From/To since the whole point of a lineageId is that it doesn't change in a passthrough.</summary>
    public required string LineageId { get; init; }

    public required string ToComponentRefId { get; init; }
    public required string ToComponentName { get; init; }
    public required string ToColumnRefId { get; init; }
    public required string ToColumnName { get; init; }

    /// <summary>"PathFlow" (unmodified passthrough of an existing lineageId into a consuming inputColumn) or "ExpressionDerived" (a new output column computed from one or more upstream lineageIds referenced in its Expression).</summary>
    public required string Kind { get; init; }

    /// <summary>Populated only for <c>ExpressionDerived</c> edges: the producing column's contribution as written in the friendly expression (or raw, if no friendly form exists). Not a full parse of multi-input expressions -- if a column combines N upstream columns, N edges are emitted, each carrying the *same* whole expression text, since the source XML doesn't segment which part of the expression came from which reference.</summary>
    public string? Expression { get; init; }
}

/// <summary>A single output column reference, used for <see cref="LineageSpec.ConstantColumns"/> and <see cref="LineageSpec.UnusedColumns"/>.</summary>
public sealed class LineageColumnRefSpec
{
    public required string ComponentRefId { get; init; }
    public required string ComponentName { get; init; }
    public required string ColumnRefId { get; init; }
    public required string ColumnName { get; init; }
    public required string LineageId { get; init; }

    /// <summary>Populated for <see cref="LineageSpec.ConstantColumns"/> only.</summary>
    public string? Expression { get; init; }
}
