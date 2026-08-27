using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Codegen;

/// <summary>One Data Flow Task, in execution order, with the components the other emitters
/// need already located. Exactly one of <see cref="FlatFileSource"/>/<see cref="OleDbSource"/>
/// is populated for a flow PackageGenerator can wire up (both null is possible too -- an
/// unsupported/absent source is a gate PackageGenerator reports, not this planner).
/// <see cref="ConditionalSplit"/> is populated instead of a single destination being the whole
/// story when this flow fans out to N destinations -- <see cref="DestinationComponent"/> still
/// points at the split's own DEFAULT branch's destination in that case (kept non-null so
/// existing single-destination code paths that only look at it stay harmless; PackageGenerator
/// checks <see cref="ConditionalSplit"/> first and branches before ever relying on that for
/// real in the split case).</summary>
public sealed record DataFlowPlan(
    string TaskName,
    PipelineSpec Pipeline,
    PipelineComponentSpec? FlatFileSource,
    PipelineComponentSpec? OleDbSource,
    PipelineComponentSpec? DerivedColumn,
    PipelineComponentSpec? Lookup,
    ConditionalSplitPlan? ConditionalSplit,
    PipelineComponentSpec DestinationComponent);

/// <summary>One non-default, non-error branch of a Conditional Split resolves to a
/// <see cref="FriendlyExpression"/> (case) or null (the default, no condition of its own) plus
/// the OLE DB Destination directly downstream of that output -- <see cref="Destination"/> is
/// resolved by a one-hop walk of the pipeline's own &lt;paths&gt; from the output's RefId, the
/// same evidenced StartId/EndId pattern RulesEngine.cs already uses for wiring checks.</summary>
public sealed record ConditionalSplitBranchPlan(string OutputName, string? FriendlyExpression, PipelineComponentSpec Destination);

/// <summary>One Conditional Split, fully resolved: every case in EvaluationOrder, THEN the
/// default branch last (<see cref="Branches"/>[^1]) -- this ordering is what RouterEmitter and
/// ProgramEmitter both rely on to build the if/else-if chain and the branch-index list without
/// re-deriving which entry is the default.</summary>
public sealed record ConditionalSplitPlan(PipelineComponentSpec Component, List<ConditionalSplitBranchPlan> Branches);

/// <summary>One step after pre-load: either a Data Flow Task or an Execute SQL Task that runs
/// AFTER at least one Data Flow Task has already run (a pre-load Execute SQL Task never
/// appears here -- it only ever lands in <see cref="PackagePlan.PreLoadStatements"/>).</summary>
public abstract record PackageStep;
public sealed record FlowStep(DataFlowPlan Flow) : PackageStep;
public sealed record SqlStep(string TaskName, string Sql) : PackageStep;

/// <summary><see cref="Flows"/> is kept alongside <see cref="Steps"/>, duplicating every
/// FlowStep's own DataFlowPlan, purely so callers that only ever cared about "all the flows,
/// in order" (most existing tests and emitters) don't need to migrate to pattern-matching
/// Steps.</summary>
public sealed record PackagePlan(List<string> PreLoadStatements, List<DataFlowPlan> Flows, List<PackageStep> Steps, List<GenerationGap> Gaps);

/// <summary>
/// Walks one package's whole control-flow tree (<c>package.Executables</c>/<c>package.Dag</c>,
/// recursing into any Sequence Container's own <c>Children</c>/<c>Dag</c>) into the ordered
/// facts <see cref="ProgramEmitter"/> and friends need: pre-load SQL text, each Data Flow
/// Task's key components, and any Execute SQL Task that runs AFTER a Data Flow Task (an
/// <see cref="SqlStep"/>, in <see cref="PackagePlan.Steps"/> at its real position -- unlike a
/// pre-load statement, this is not hoisted to the front), in the exact order SSIS would run
/// them.
///
/// Pre-load vs. post-flow is decided from each container's own <c>PrecedenceConstraints</c>
/// (direct predecessor lookup), NOT from a single global "have we seen any flow yet" flag --
/// that simpler design was tried first and rejected after it produced a wrong answer on this
/// tool's own SyntheticParallelShapes.dtsx fixture: SQL_CreateLoadATemp has no precedence
/// relationship at all to the package's other two (fully independent) top-level branches, but
/// a global flag still misclassified it as post-flow purely because DFT_DirectCopy/
/// DFT_ErrorRouting happened to sort earlier in the merged topological order. An Execute SQL
/// Task with no predecessor in its own container inherits whether the CONTAINER itself is
/// already running after a flow (relevant once a Sequence Container is itself downstream of
/// an outer flow); one with predecessors is post-flow iff any direct predecessor is a Data
/// Flow Task or is itself already post-flow -- computed in a single forward pass since
/// <c>dag.TopologicalOrder</c> already guarantees every predecessor is visited first. A
/// container's own "did a flow happen anywhere inside me" result is returned to its caller so
/// a Sequence Container correctly propagates to ITS OWN siblings in the parent container, the
/// same way a bare Data Flow Task would.
///
/// A Sequence Container (<c>STOCK:SEQUENCE</c>) is transparent -- its own children are walked
/// in ITS topological order and folded into the same flat lists, at whatever point the
/// container itself falls in its parent's order. It carries no execution semantics of its own
/// beyond grouping and ordering (confirmed via an isolated object-model probe before writing
/// this: a freshly built Sequence Container's own saved XML has no payload, just
/// <c>&lt;DTS:Executables&gt;</c>/<c>&lt;DTS:PrecedenceConstraints&gt;</c>, exactly the shape
/// <c>DtsxPackageReader.ReadContainerBody</c> already reads generically for every container --
/// see the synthetic fixture this was verified against,
/// <c>tests/Ssis.Extract.Tests/Fixtures/SyntheticNestedContainer.dtsx</c>).
///
/// A ForEach/For Loop container (<c>STOCK:FOREACHLOOP</c>) is NOT walked the same way and
/// reports its own explicit gap instead -- unlike Sequence, a loop has real per-iteration
/// semantics (an enumerator, variable mappings) that change what "run these children" means,
/// and <c>Etl.Core</c> has no loop abstraction to generate against (plan's own scope table).
/// Silently flattening a loop's children would generate code that runs each of them exactly
/// once instead of per iteration -- wrong, not just incomplete.
///
/// An executable type that is none of Sequence/ForEach/Data Flow/Execute SQL, or a Data Flow
/// Task with no OLE DB Destination, is reported as a gap rather than silently skipped.
/// </summary>
public static class PackagePlanner
{
    public static PackagePlan Plan(PackageSpec package)
    {
        var gaps = new List<GenerationGap>();
        var preLoadStatements = new List<string>();
        var flows = new List<DataFlowPlan>();
        var steps = new List<PackageStep>();

        WalkContainer(package.Executables, package.Dag, package.PrecedenceConstraints,
            preLoadStatements, flows, steps, gaps, containerIsPostFlow: false);

        return new PackagePlan(preLoadStatements, flows, steps, gaps);
    }

    /// <summary>Returns true if, by the time this container finishes (from its parent's point
    /// of view), a Data Flow Task has run -- either because <paramref name="containerIsPostFlow"/>
    /// was already true coming in, or because one ran somewhere inside.</summary>
    private static bool WalkContainer(
        List<ExecutableSpec> children, ControlFlowDagSpec dag, List<PrecedenceConstraintSpec> constraints,
        List<string> preLoadStatements, List<DataFlowPlan> flows, List<PackageStep> steps,
        List<GenerationGap> gaps, bool containerIsPostFlow)
    {
        var byRefId = children.ToDictionary(e => e.RefId);

        // dag.TopologicalOrder is empty by design for 0-1 children (that type's own doc
        // comment) -- fall back to declaration order in that case.
        var order = dag.TopologicalOrder.Count > 0
            ? dag.TopologicalOrder
            : children.Select(e => e.RefId).ToList();

        var predecessorsByRefId = new Dictionary<string, List<string>>();
        foreach (var c in constraints)
        {
            if (!byRefId.ContainsKey(c.From) || !byRefId.ContainsKey(c.To)) continue;
            if (!predecessorsByRefId.TryGetValue(c.To, out var list))
                predecessorsByRefId[c.To] = list = [];
            list.Add(c.From);
        }

        // refIds (within THIS container only) known to be a Data Flow Task or to already run
        // after one -- built up as we go, in topological order, so every predecessor is
        // resolved before its successors are evaluated.
        var flowOrAfter = new HashSet<string>();
        var sawDataFlowInThisContainer = false;

        foreach (var refId in order)
        {
            if (!byRefId.TryGetValue(refId, out var executable))
            {
                gaps.Add(new GenerationGap(refId, "topological order references an executable not found in its own container"));
                continue;
            }

            var isPostFlow = predecessorsByRefId.TryGetValue(refId, out var preds)
                ? preds.Any(flowOrAfter.Contains)
                : containerIsPostFlow;

            if (executable.ExecutableType == "STOCK:SEQUENCE")
            {
                var childRanAfterFlow = WalkContainer(executable.Children, executable.Dag, executable.PrecedenceConstraints,
                    preLoadStatements, flows, steps, gaps, containerIsPostFlow: isPostFlow);
                if (childRanAfterFlow)
                {
                    flowOrAfter.Add(refId);
                    sawDataFlowInThisContainer = true;
                }
                continue;
            }

            if (executable.ExecutableType == "STOCK:FOREACHLOOP")
            {
                gaps.Add(new GenerationGap(executable.ObjectName ?? refId,
                    "ForEach Loop containers are not supported -- Etl.Core has no loop abstraction to generate against"));
                continue;
            }

            if (executable.ExecuteSqlTask is { } sqlTask)
            {
                if (string.IsNullOrWhiteSpace(sqlTask.SqlStatementSource))
                {
                    gaps.Add(new GenerationGap(executable.ObjectName ?? refId, "Execute SQL Task has no SqlStatementSource"));
                    continue;
                }

                if (isPostFlow)
                {
                    steps.Add(new SqlStep(executable.ObjectName ?? refId, sqlTask.SqlStatementSource));
                    flowOrAfter.Add(refId);
                }
                else
                {
                    preLoadStatements.Add(sqlTask.SqlStatementSource);
                }
                continue;
            }

            if (executable.DataFlowTask is { } dataFlowTask)
            {
                var flow = PlanDataFlow(executable.ObjectName ?? refId, dataFlowTask.Pipeline, gaps);
                if (flow is not null)
                {
                    flows.Add(flow);
                    steps.Add(new FlowStep(flow));
                    flowOrAfter.Add(refId);
                    sawDataFlowInThisContainer = true;
                }
                continue;
            }

            gaps.Add(new GenerationGap(executable.ObjectName ?? refId,
                $"executable type '{executable.ExecutableType}' is neither a Data Flow Task nor an Execute SQL Task -- not supported by this planner yet"));
        }

        return containerIsPostFlow || sawDataFlowInThisContainer;
    }

    private static DataFlowPlan? PlanDataFlow(string taskName, PipelineSpec pipeline, List<GenerationGap> gaps)
    {
        var flatFileSource = pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.FlatFileSource");
        var oleDbSource = pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.OLEDBSource");
        var derivedColumn = pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.DerivedColumn");
        var lookup = pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.Lookup");
        var conditionalSplitComponent = pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.ConditionalSplit");

        ConditionalSplitPlan? conditionalSplit = null;
        if (conditionalSplitComponent is not null)
        {
            conditionalSplit = PlanConditionalSplit(taskName, pipeline, conditionalSplitComponent, gaps);
            if (conditionalSplit is null) return null; // PlanConditionalSplit already added the gap
        }

        var destination = conditionalSplit is not null
            ? conditionalSplit.Branches[^1].Destination // the default branch, resolved last
            : pipeline.Components.FirstOrDefault(c => c.ComponentClassId == "Microsoft.OLEDBDestination");

        if (destination is null)
        {
            gaps.Add(new GenerationGap(taskName,
                "no OLE DB Destination found in this Data Flow Task -- only [Flat File Source|OLE DB Source] -> [Derived Column] -> OLE DB Destination is supported yet"));
            return null;
        }

        return new DataFlowPlan(taskName, pipeline, flatFileSource, oleDbSource, derivedColumn, lookup, conditionalSplit, destination);
    }

    /// <summary>Resolves every case (in EvaluationOrder) plus the default output to the OLE DB
    /// Destination directly downstream of it. Fatal for the whole flow if ANY branch fails to
    /// resolve -- no partial split generation, matching the same "no half-finished
    /// implementations" rule Lookup's own gate follows.</summary>
    private static ConditionalSplitPlan? PlanConditionalSplit(
        string taskName, PipelineSpec pipeline, PipelineComponentSpec splitComponent, List<GenerationGap> gaps)
    {
        var payload = splitComponent.ConditionalSplit;
        if (payload?.DefaultOutputName is null)
        {
            gaps.Add(new GenerationGap(taskName,
                $"Conditional Split '{splitComponent.Name}' has no recognizable default output -- not supported"));
            return null;
        }

        var branches = new List<ConditionalSplitBranchPlan>();
        foreach (var @case in payload.Cases)
        {
            var branch = ResolveBranch(pipeline, splitComponent, @case.OutputName, @case.FriendlyExpression, taskName, gaps);
            if (branch is null) return null; // ResolveBranch already added the gap
            branches.Add(branch);
        }

        var defaultBranch = ResolveBranch(pipeline, splitComponent, payload.DefaultOutputName, friendlyExpression: null, taskName, gaps);
        if (defaultBranch is null) return null;
        branches.Add(defaultBranch);

        return new ConditionalSplitPlan(splitComponent, branches);
    }

    private static ConditionalSplitBranchPlan? ResolveBranch(
        PipelineSpec pipeline, PipelineComponentSpec splitComponent, string outputName, string? friendlyExpression,
        string taskName, List<GenerationGap> gaps)
    {
        var output = splitComponent.Outputs.FirstOrDefault(o => o.Name == outputName);
        if (output is null)
        {
            gaps.Add(new GenerationGap(taskName,
                $"Conditional Split output '{outputName}' on '{splitComponent.Name}' was not found among its own outputs"));
            return null;
        }

        var path = pipeline.Paths.FirstOrDefault(p => p.StartId == output.RefId);
        var destination = path is null ? null : pipeline.Components.FirstOrDefault(c =>
            c.ComponentClassId == "Microsoft.OLEDBDestination" && c.Inputs.Any(i => i.RefId == path.EndId));

        if (destination is null)
        {
            gaps.Add(new GenerationGap(taskName,
                $"Conditional Split output '{outputName}' on '{splitComponent.Name}' does not connect directly to an OLE DB Destination -- not supported"));
            return null;
        }

        return new ConditionalSplitBranchPlan(outputName, friendlyExpression, destination);
    }
}
