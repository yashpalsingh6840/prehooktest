namespace Ssis.Extract.Model.Analysis;

/// <summary>
/// One row of the non-determinism manifest (plan §5.8) -- the explicit phase-0 → phase-2
/// handoff that lets a later parallel-run diff harness exclude these columns from its row
/// hash automatically instead of someone hand-maintaining an exclusion list. Derived by
/// <c>Ssis.Extract.Dtsx.NonDeterminismAnalyzer</c> from <c>LineageSpec.ConstantColumns</c>,
/// filtered to the specific non-deterministic function patterns the plan names
/// (<c>GETDATE()</c>/<c>GETUTCDATE()</c>/<c>NEWID()</c>/<c>@[System::...]</c>), then traced
/// forward to whichever OLE DB Destination ultimately lands the value.
///
/// <b>Known gap, stated rather than silently absent:</b> the plan also names identity
/// columns as a non-determinism source. Identity is a destination-table property, not
/// anything present in a package's own XML, so it cannot be detected from extraction alone
/// -- it needs the live database schema (a later-slice concern, e.g. plan §5.3/§6's
/// <c>enrich</c>). Not included here.
/// </summary>
public sealed class NonDeterministicColumnSpec
{
    public required string PackageName { get; init; }

    /// <summary>The Data Flow Task's own path within the control-flow tree, e.g. "DFT_LoadEmployees".</summary>
    public required string DataFlowTaskPath { get; init; }

    /// <summary>Resolved from the landing OLE DB Destination's <c>OpenRowset</c> property. Null if the non-deterministic value is produced but never traced to a destination (see <see cref="LandingComponentName"/>'s doc comment on why the trace can fail to reach one).</summary>
    public string? TargetTable { get; init; }

    public string? TargetColumn { get; init; }

    public required string SourceComponentName { get; init; }
    public required string SourceColumnName { get; init; }
    public required string Expression { get; init; }

    /// <summary>Which pattern matched: "GETDATE" | "GETUTCDATE" | "NEWID" | "SystemVariable".</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// The component the value was traced to, if any. Tracing only follows "PathFlow"
    /// lineage edges (unmodified passthrough) forward from the source column -- it
    /// deliberately does not cross an "ExpressionDerived" edge (the value being fed into a
    /// further expression means a *new*, separately-tracked lineageId takes over from
    /// there), and it only recognizes a landing site when it reaches a component with an
    /// <c>OleDbDestination</c> payload. Neither PoC package's non-deterministic columns
    /// exercise the "never reaches a destination" case, so <see cref="TargetTable"/> is
    /// always populated for them -- the null case is real schema, not a guess.
    /// </summary>
    public string? LandingComponentName { get; init; }
}
