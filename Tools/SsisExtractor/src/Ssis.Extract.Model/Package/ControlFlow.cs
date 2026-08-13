namespace Ssis.Extract.Model.Package;

/// <summary>A <c>DTS:PrecedenceConstraint</c> (plan §4.6) -- one ordering edge between two sibling executables in the same container.</summary>
public sealed class PrecedenceConstraintSpec
{
    public string? RefId { get; init; }
    public string? DtsId { get; init; }
    public string? ObjectName { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }

    /// <summary>Success/Failure/Completion. Null means the schema default (Success) applies -- SSIS omits this attribute when it's the default, same "absent is not an error" convention as every other reader in this project.</summary>
    public string? Value { get; init; }

    /// <summary>Constraint/Expression/ExpressionAndConstraint/ExpressionOrConstraint. Null means the schema default (Constraint, i.e. no expression) applies.</summary>
    public string? EvalOp { get; init; }
    public string? Expression { get; init; }
    public bool? LogicalAnd { get; init; }
}

/// <summary>
/// The derived DAG over one container's direct children and precedence constraints (plan
/// §4.5's "Derived: build the DAG per container" bullet). Computed generically by
/// <c>Ssis.Extract.Dtsx.DtsxPackageReader</c> via Kahn's algorithm; not modeled after any
/// evidence in this PoC's own two-node/one-edge fixtures (too trivial to exercise
/// branching), so its correctness is instead proven by a synthetic unit test
/// (<c>tests/Ssis.Extract.Tests/DagAlgorithmTests.cs</c>) rather than a golden file.
/// </summary>
public sealed class ControlFlowDagSpec
{
    /// <summary>Children's refIds in one valid topological order. Empty when there are 0-1 children, or when <see cref="HasCycle"/> is true.</summary>
    public List<string> TopologicalOrder { get; init; } = [];

    /// <summary>
    /// Children's refIds grouped by "wave" via longest-path layering: level(n) = 0 for a
    /// node with no incoming constraint, else 1 + max(level(predecessor)) over its direct
    /// predecessors. Two nodes in the same level are provably unreachable from each other
    /// in either direction (a constraint always strictly increases the target's level), so
    /// a level is a mathematically sound "these could run in parallel" grouping.
    ///
    /// This is a SOUND BUT INCOMPLETE accounting of parallelism, by design: two nodes in
    /// *different* levels can also happen to have no ordering between them (e.g. two
    /// independent chains of different lengths), and this representation won't surface
    /// that pair. Reporting the complete all-pairs "no path either direction" relation
    /// would need O(n^2) pairs and isn't a valid partition (pairwise incomparability isn't
    /// transitive), so it can't be grouped the same way without being misleading. Levels
    /// trade completeness for a result that's cheap to compute and safe to read literally:
    /// every pair this reports as parallel really is; it just isn't the only such pairing.
    /// </summary>
    public List<List<string>> ParallelLevels { get; init; } = [];

    /// <summary>True if the constraints don't form a DAG (a cycle exists among this container's own children). SSIS's designer prevents authoring this, but a hand-edited or corrupted file could still contain one -- reported rather than looping forever or throwing.</summary>
    public bool HasCycle { get; init; }
}
