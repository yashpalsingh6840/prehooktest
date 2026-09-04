using Ssis.Extract.Model.Package;

namespace Svk.Core;

/// <summary>One executable in real SSIS execution order, annotated with how deep it is nested
/// and, when it ran concurrently with siblings (Dag.ParallelLevels), which group and how big.</summary>
public sealed record ExecutionStep(ExecutableSpec Executable, int Depth, int? ParallelGroupIndex, int ParallelGroupSize);

/// <summary>
/// Walks a package's own control-flow tree in Dag.TopologicalOrder, recursing into nested
/// containers via each one's own Dag (scoped per-container, not global -- see
/// ControlFlowDagSpec's own doc comment). Reads data that already exists in spec.json; adds no
/// new extraction.
/// </summary>
public static class ExecutionOrderBuilder
{
    public static List<ExecutionStep> Build(PackageSpec package)
    {
        var steps = new List<ExecutionStep>();
        Walk(package.Executables, package.Dag, depth: 0, steps);
        return steps;
    }

    private static void Walk(List<ExecutableSpec> children, ControlFlowDagSpec dag, int depth, List<ExecutionStep> steps)
    {
        if (children.Count == 0) return;

        var byRefId = children.ToDictionary(c => c.RefId);
        var order = dag.TopologicalOrder.Count > 0 ? dag.TopologicalOrder : children.Select(c => c.RefId).ToList();

        var groups = new Dictionary<string, (int Index, int Size)>();
        for (var i = 0; i < dag.ParallelLevels.Count; i++)
        {
            var level = dag.ParallelLevels[i];
            if (level.Count <= 1) continue;
            foreach (var refId in level) groups[refId] = (i, level.Count);
        }

        foreach (var refId in order)
        {
            if (!byRefId.TryGetValue(refId, out var ex)) continue;

            int? groupIndex = null;
            var groupSize = 0;
            if (groups.TryGetValue(refId, out var g))
            {
                groupIndex = g.Index;
                groupSize = g.Size;
            }

            steps.Add(new ExecutionStep(ex, depth, groupIndex, groupSize));
            if (ex.Children.Count > 0)
            {
                Walk(ex.Children, ex.Dag, depth + 1, steps);
            }
        }
    }
}
