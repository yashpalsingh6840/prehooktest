using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Dtsx;

/// <summary>One physical component-to-component wire, derived from <see cref="PipelinePathSpec"/>
/// (the buffer connection), not <see cref="LineageSpec"/> (column-level value provenance, which
/// by design skips straight past a passthrough component like Multicast to the true producer --
/// see that type's own doc comment). <see cref="ColumnCount"/> is the receiving input's own
/// column count, which is meaningful even for a passthrough hop.</summary>
public sealed record ComponentFlowEdge(string FromRefId, string FromName, string ToRefId, string ToName, int ColumnCount);

/// <summary>
/// <see cref="OrderedComponentRefIds"/> is every component in <see cref="PipelineSpec.Components"/>,
/// sorted by <see cref="LevelByRefId"/> (ties broken by name for determinism) -- the single source
/// of truth every consumer of this ordering reads (the SsisValidationKit walkthrough/diagram
/// builders, and -- since the emitter rewrite -- <c>PackageClassEmitter</c>'s own per-component
/// method ordering), so a diagram node's number, a walkthrough table row's "Seq", and a generated
/// method's doc-comment number can never drift apart.
///
/// <see cref="LevelByRefId"/> is longest-path layering over <see cref="Edges"/> (1-based: level 1
/// for a component with no incoming edge -- a true source -- else 1 + max(level of its direct
/// predecessors)), the exact same technique <c>Ssis.Extract.Dtsx</c> already uses for
/// <c>ControlFlowDagSpec.ParallelLevels</c>, applied here to the pipeline's own physical wiring
/// instead of precedence constraints. Two components in the same level are provably unreachable
/// from each other in either direction (a path always strictly increases the target's level) --
/// e.g. two branches fanned out from the same Multicast/Conditional Split -- so giving them the
/// SAME number is the honest picture: neither runs "before" the other, and numbering them 3/4
/// would have implied an ordering the pipeline itself never declares.
/// </summary>
public sealed record PipelineFlow(List<string> OrderedComponentRefIds, List<ComponentFlowEdge> Edges, IReadOnlyDictionary<string, int> LevelByRefId);

/// <summary>
/// Computes real data-flow order for one Data Flow Task's components -- <c>spec.json</c> itself
/// gives no such order (<see cref="PipelineSpec.Components"/> comes back sorted alphabetically by
/// name, the same deterministic-output convention every other extractor list uses, but alphabetic
/// order has nothing to do with which component runs before which). Built the same way
/// <c>ExecutionOrderBuilder</c> derives real control-flow order from precedence constraints, just
/// over the pipeline's own physical buffer wiring instead.
/// </summary>
public static class PipelineFlowOrder
{
    public static PipelineFlow Compute(PipelineSpec pipeline)
    {
        var outputOwner = new Dictionary<string, PipelineComponentSpec>();
        var inputOwner = new Dictionary<string, PipelineComponentSpec>();
        foreach (var comp in pipeline.Components)
        {
            foreach (var o in comp.Outputs) outputOwner[o.RefId] = comp;
            foreach (var i in comp.Inputs) inputOwner[i.RefId] = comp;
        }

        var edges = new List<ComponentFlowEdge>();
        foreach (var path in pipeline.Paths)
        {
            if (!outputOwner.TryGetValue(path.StartId, out var fromComp)) continue;
            if (!inputOwner.TryGetValue(path.EndId, out var toComp)) continue;
            if (fromComp.RefId == toComp.RefId) continue; // shouldn't happen; guards against a malformed path being read as a self-loop

            var input = toComp.Inputs.FirstOrDefault(i => i.RefId == path.EndId);
            edges.Add(new ComponentFlowEdge(fromComp.RefId, fromComp.Name, toComp.RefId, toComp.Name, input?.Columns.Count ?? 0));
        }

        // One edge per distinct (producer, consumer) pair -- two paths between the same two
        // components isn't evidenced anywhere in this project's own fixtures, but dedupe
        // defensively rather than emit a duplicate Mermaid arrow.
        var dedupedEdges = edges.GroupBy(e => (e.FromRefId, e.ToRefId)).Select(g => g.First()).ToList();

        var allIds = pipeline.Components.Select(c => c.RefId).ToList();
        var nameById = pipeline.Components.ToDictionary(c => c.RefId, c => c.Name);
        var topoOrder = TopologicalOrder(allIds, nameById, dedupedEdges);
        var level = ComputeLevels(topoOrder, dedupedEdges);

        var ordered = topoOrder
            .OrderBy(id => level[id])
            .ThenBy(id => nameById[id], StringComparer.Ordinal)
            .ToList();

        return new PipelineFlow(ordered, dedupedEdges, level);
    }

    /// <summary>Kahn's algorithm -- the same approach <c>Ssis.Extract.Dtsx.DtsxPackageReader.BuildDag</c>
    /// already uses for control flow. This pass only needs SOME valid topological order (ties
    /// broken by name purely for deterministic output, not a claim about real SSIS
    /// buffer-processing order) -- <see cref="ComputeLevels"/> is what turns it into the
    /// level-grouped numbering actually shown to a reviewer. A component reachable by no path at
    /// all (isolated -- not evidenced in this project's own fixtures, but a Row Count/error-
    /// output-only shape could produce one) still gets an entry: it starts with in-degree 0 like
    /// a true source, so it's placed at level 1 rather than silently dropped from the table.</summary>
    private static List<string> TopologicalOrder(List<string> allIds, Dictionary<string, string> nameById, List<ComponentFlowEdge> edges)
    {
        var adjacency = allIds.ToDictionary(id => id, _ => new List<string>());
        var inDegree = allIds.ToDictionary(id => id, _ => 0);

        foreach (var e in edges)
        {
            adjacency[e.FromRefId].Add(e.ToRefId);
            inDegree[e.ToRefId]++;
        }

        int TieBreak(string a, string b)
        {
            var byName = string.CompareOrdinal(nameById[a], nameById[b]);
            return byName != 0 ? byName : string.CompareOrdinal(a, b);
        }

        var ready = new SortedSet<string>(allIds.Where(id => inDegree[id] == 0), Comparer<string>.Create(TieBreak));
        var remaining = new Dictionary<string, int>(inDegree);
        var order = new List<string>();

        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            order.Add(id);
            foreach (var next in adjacency[id])
            {
                if (--remaining[next] == 0) ready.Add(next);
            }
        }

        // A cycle (not evidenced anywhere, but a hand-edited/corrupted .dtsx could contain one --
        // same defensive posture as ControlFlowDagSpec.HasCycle) leaves nodes unplaced. Append
        // them rather than silently dropping a component from the table.
        var placed = new HashSet<string>(order);
        order.AddRange(allIds.Where(id => !placed.Contains(id)).OrderBy(id => nameById[id], StringComparer.Ordinal));

        return order;
    }

    /// <summary>1-based longest-path layering. <paramref name="topoOrder"/> guarantees every
    /// predecessor is processed before its consumer, so one forward pass suffices.</summary>
    private static Dictionary<string, int> ComputeLevels(List<string> topoOrder, List<ComponentFlowEdge> edges)
    {
        var predecessorsOf = topoOrder.ToDictionary(id => id, _ => new List<string>());
        foreach (var e in edges)
        {
            if (predecessorsOf.TryGetValue(e.ToRefId, out var preds)) preds.Add(e.FromRefId);
        }

        var level = new Dictionary<string, int>();
        foreach (var id in topoOrder)
        {
            var preds = predecessorsOf[id];
            level[id] = preds.Count == 0 ? 1 : preds.Max(p => level[p]) + 1;
        }

        return level;
    }
}
