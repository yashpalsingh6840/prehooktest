using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Builds the non-determinism manifest (plan §5.8) from every Data Flow Task's already-
/// derived <see cref="LineageSpec.ConstantColumns"/>, filtered to the specific function
/// patterns the plan names, then traced forward to whichever <c>OleDbDestination</c>
/// ultimately lands the value -- see <see cref="NonDeterministicColumnSpec"/>'s doc comment
/// for why the trace only follows "PathFlow" edges.
/// </summary>
public static class NonDeterminismAnalyzer
{
    public static List<NonDeterministicColumnSpec> Analyze(PackageSpec package, List<ExecutableSpec> allExecutables)
    {
        var results = new List<NonDeterministicColumnSpec>();

        foreach (var ex in allExecutables.Where(e => e.DataFlowTask is not null))
        {
            var dftPath = ex.ObjectName ?? ex.RefId;
            var pipeline = ex.DataFlowTask!.Pipeline;
            var lineage = ex.DataFlowTask.Lineage;

            foreach (var constant in lineage.ConstantColumns)
            {
                var reason = ClassifyReason(constant.Expression);
                if (reason is null) continue; // a genuine literal constant, not a non-determinism source

                var destinations = TraceToDestinations(pipeline, lineage, constant.ColumnRefId).ToList();

                if (destinations.Count == 0)
                {
                    results.Add(new NonDeterministicColumnSpec
                    {
                        PackageName = package.ObjectName,
                        DataFlowTaskPath = dftPath,
                        SourceComponentName = constant.ComponentName,
                        SourceColumnName = constant.ColumnName,
                        Expression = constant.Expression ?? "",
                        Reason = reason,
                        TargetTable = null,
                        TargetColumn = null,
                        LandingComponentName = null,
                    });
                    continue;
                }

                foreach (var (destComponent, destColumn) in destinations)
                {
                    var mapping = destComponent.OleDbDestination?.ColumnMappings.FirstOrDefault(m => m.ComponentColumnName == destColumn.CachedName);
                    results.Add(new NonDeterministicColumnSpec
                    {
                        PackageName = package.ObjectName,
                        DataFlowTaskPath = dftPath,
                        SourceComponentName = constant.ComponentName,
                        SourceColumnName = constant.ColumnName,
                        Expression = constant.Expression ?? "",
                        Reason = reason,
                        TargetTable = destComponent.OleDbDestination?.OpenRowset,
                        TargetColumn = mapping?.ExternalColumnName,
                        LandingComponentName = destComponent.Name,
                    });
                }
            }
        }

        return results;
    }

    private static string? ClassifyReason(string? expression)
    {
        if (expression is null) return null;
        if (expression.Contains("GETUTCDATE(", StringComparison.OrdinalIgnoreCase)) return "GETUTCDATE";
        if (expression.Contains("GETDATE(", StringComparison.OrdinalIgnoreCase)) return "GETDATE";
        if (expression.Contains("NEWID(", StringComparison.OrdinalIgnoreCase)) return "NEWID";
        if (expression.Contains("@[System::", StringComparison.Ordinal)) return "SystemVariable";
        return null;
    }

    /// <summary>Breadth-first search forward through "PathFlow" lineage edges only, from <paramref name="startColumnRefId"/>, stopping at (and yielding) every input column belonging to a component with an <c>OleDbDestination</c> payload it reaches. Does not cross an "ExpressionDerived" edge -- see <see cref="NonDeterministicColumnSpec"/>'s doc comment.</summary>
    private static IEnumerable<(PipelineComponentSpec Component, PipelineInputColumnSpec Column)> TraceToDestinations(PipelineSpec pipeline, LineageSpec lineage, string startColumnRefId)
    {
        var componentsByRefId = pipeline.Components.ToDictionary(c => c.RefId);
        var visited = new HashSet<string> { startColumnRefId };
        var queue = new Queue<string>();
        queue.Enqueue(startColumnRefId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in lineage.Edges.Where(e => e.Kind == "PathFlow" && e.FromColumnRefId == current))
            {
                if (!visited.Add(edge.ToColumnRefId)) continue;

                if (componentsByRefId.TryGetValue(edge.ToComponentRefId, out var comp) && comp.OleDbDestination is not null)
                {
                    var col = comp.Inputs.SelectMany(i => i.Columns).FirstOrDefault(c => c.RefId == edge.ToColumnRefId);
                    if (col is not null)
                    {
                        yield return (comp, col);
                        continue; // a destination is terminal -- don't keep searching past it
                    }
                }

                queue.Enqueue(edge.ToColumnRefId);
            }
        }
    }
}
