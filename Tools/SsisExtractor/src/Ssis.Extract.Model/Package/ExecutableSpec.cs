using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Model.Package;

/// <summary>
/// One node in the control-flow tree (plan §4.5) -- a container or a leaf task. The
/// package root itself is walked with the same code (it's a <c>DTS:Executable</c> too),
/// so <see cref="PackageSpec.Executables"/>/<see cref="PackageSpec.PrecedenceConstraints"/>/
/// <see cref="PackageSpec.Dag"/> are just the root container's own
/// <see cref="Children"/>/<see cref="PrecedenceConstraints"/>/<see cref="Dag"/>.
///
/// Attribute naming note: the package root uses <c>DTS:Disable</c>/<c>DTS:ForceExecutionResult</c>
/// (see <see cref="PackageSpec.ExecutionSemantics"/>); every other executable in the tree
/// uses <c>DTS:Disabled</c>/<c>DTS:ForcedExecutionValue</c> instead -- a genuine schema
/// difference, not a typo, so the two are modeled as separate fields rather than unified.
/// </summary>
public sealed class ExecutableSpec
{
    public required string RefId { get; init; }
    public string? ObjectName { get; init; }
    public string? Description { get; init; }
    public string? DtsId { get; init; }

    /// <summary>
    /// The task/container type, e.g. "Microsoft.ExecuteSQLTask", "Microsoft.Pipeline",
    /// "STOCK:SEQUENCE" (not in this PoC). Read from <c>DTS:ExecutableType</c>, falling
    /// back to <c>DTS:CreationName</c> if absent -- both are present with the same value
    /// on every executable in this PoC's fixtures.
    /// </summary>
    public required string ExecutableType { get; init; }

    public bool? Disabled { get; init; }
    public bool? DelayValidation { get; init; }
    public bool? FailParentOnFailure { get; init; }
    public bool? FailPackageOnFailure { get; init; }
    public int? MaximumErrorCount { get; init; }
    public string? TransactionOption { get; init; }
    public string? IsolationLevel { get; init; }
    public string? ForcedExecutionValue { get; init; }
    public string? ThreadHint { get; init; }

    public List<PropertyExpressionSpec> PropertyExpressions { get; init; } = [];

    /// <summary>Variables declared directly on this container's own scope (plan §4.4) -- not resolved/inherited across descendants here; that's a consumer's job.</summary>
    public List<VariableSpec> Variables { get; init; } = [];

    /// <summary>Nested executables. Populated for containers (Sequence, ForEach/For Loop -- neither present in this PoC); empty for a leaf task.</summary>
    public List<ExecutableSpec> Children { get; init; } = [];

    /// <summary>This container's own precedence constraints, ordering just its direct <see cref="Children"/> (plan §4.6). A nested container's constraints never reference a node outside its own <see cref="Children"/>.</summary>
    public List<PrecedenceConstraintSpec> PrecedenceConstraints { get; init; } = [];

    /// <summary>Derived DAG over <see cref="Children"/>/<see cref="PrecedenceConstraints"/> -- present (possibly empty) even for a leaf task with no children.</summary>
    public ControlFlowDagSpec Dag { get; init; } = new();

    public List<EventHandlerSpec> EventHandlers { get; init; } = [];

    // At most one of the payload fields below is non-null, chosen by ExecutableType -- see
    // docs/spec-schema.md for the discriminator table. A plain container (e.g. Sequence, not
    // evidenced in this PoC) with no DTS:ObjectData of its own and no bespoke sibling element
    // gets none of them; a ForEach Loop is the one exception that's still a real container
    // (Children/PrecedenceConstraints/Dag populated) while ALSO carrying a payload, since its
    // enumerator config lives in sibling elements alongside <Executables>, not inside
    // <ObjectData> the way every task's payload does -- see ForEachLoopPayload's own comment.
    public ExecuteSqlTaskPayload? ExecuteSqlTask { get; init; }
    public DataFlowTaskPayload? DataFlowTask { get; init; }
    public UnmappedTaskPayload? UnmappedTask { get; init; }
    public ScriptTaskPayload? ScriptTask { get; init; }
    public ForEachLoopPayload? ForEachLoop { get; init; }
    public FileSystemTaskPayload? FileSystemTask { get; init; }

    public ExecutePackageTaskPayload? ExecutePackageTask { get; init; }
}
