using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Derives <see cref="ComplexityStats"/> from an already-parsed <see cref="PackageSpec"/>
/// (plan §5.6). Pure/offline, same as <see cref="LineageBuilder"/> -- no XML here.
/// </summary>
public static class ComplexityScorer
{
    private static readonly HashSet<string> LoopTypes = ["Microsoft.ForEachLoop", "STOCK:FORLOOP", "STOCK:FOREACHLOOP"];

    public static ComplexityStats Score(PackageSpec package, ComplexityWeights weights)
    {
        var allExecutables = PackageTree.AllExecutables(package).ToList();

        var taskCount = allExecutables.Count(e => e.Children.Count == 0);
        var containerCount = allExecutables.Count(e => e.Children.Count > 0);
        var dataFlowTaskCount = allExecutables.Count(e => e.DataFlowTask is not null);
        var pipelineComponentCount = allExecutables.Where(e => e.DataFlowTask is not null).Sum(e => e.DataFlowTask!.Pipeline.Components.Count);
        var sqlStatementCount = allExecutables.Count(e => e.ExecuteSqlTask is not null);
        var scriptTaskCount = allExecutables.Count(e => e.ExecutableType is "Microsoft.ScriptTask" or "STOCK:ScriptTask");
        var scriptComponentCount = allExecutables.Where(e => e.DataFlowTask is not null)
            .Sum(e => e.DataFlowTask!.Pipeline.Components.Count(c => c.ScriptComponent is not null));
        var loopCount = allExecutables.Count(e => LoopTypes.Contains(e.ExecutableType));
        var eventHandlerCount = allExecutables.Sum(e => e.EventHandlers.Count) + package.EventHandlers.Count;
        var unmappedNodeCount = allExecutables.Count(e => e.UnmappedTask is not null);

        var expressionCount =
            package.PropertyExpressions.Count
            + package.ConnectionManagers.Sum(cm => cm.PropertyExpressions.Count)
            + allExecutables.Sum(e => e.PropertyExpressions.Count)
            + package.PrecedenceConstraints.Count(c => c.Expression is not null)
            + allExecutables.Sum(e => e.PrecedenceConstraints.Count(c => c.Expression is not null))
            + allExecutables.Where(e => e.DataFlowTask is not null)
                .Sum(e => e.DataFlowTask!.Pipeline.Components.Sum(c => c.Outputs.Sum(o => o.Columns.Count(col => col.Expression is not null))));

        var score =
            taskCount * weights.Task
            + containerCount * weights.Container
            + dataFlowTaskCount * weights.DataFlowTask
            + pipelineComponentCount * weights.PipelineComponent
            + expressionCount * weights.Expression
            + sqlStatementCount * weights.SqlStatement
            + scriptTaskCount * weights.ScriptTask
            + scriptComponentCount * weights.ScriptTask
            + loopCount * weights.Loop
            + eventHandlerCount * weights.EventHandler
            + unmappedNodeCount * weights.UnmappedNode;

        var classification = Classify(score, weights, scriptTaskCount + scriptComponentCount, unmappedNodeCount, sqlStatementCount, dataFlowTaskCount);

        return new ComplexityStats
        {
            TaskCount = taskCount,
            ContainerCount = containerCount,
            DataFlowTaskCount = dataFlowTaskCount,
            PipelineComponentCount = pipelineComponentCount,
            ExpressionCount = expressionCount,
            SqlStatementCount = sqlStatementCount,
            ScriptTaskCount = scriptTaskCount,
            ScriptComponentCount = scriptComponentCount,
            LoopCount = loopCount,
            EventHandlerCount = eventHandlerCount,
            UnmappedNodeCount = unmappedNodeCount,
            Score = Math.Round(score, 2),
            Classification = classification,
        };
    }

    /// <summary>
    /// Rule order matters -- each rule fires before the next is even considered:
    /// 1. Any script task, script component, or unmapped (unrecognized) node → <c>HighRisk</c>,
    ///    regardless of score. Custom/unknown code can't be scored away; it's always the risk driver.
    /// 2. Score at or above <see cref="ComplexityWeights.HighRiskThreshold"/> → <c>HighRisk</c>.
    /// 3. SQL statements present with zero Data Flow Tasks → <c>SqlHeavy</c>. This is a
    ///    content-shape classification, not a score threshold: a package that's pure
    ///    Execute SQL Task orchestration (no data flow) is a fundamentally different rewrite
    ///    shape than one with real pipeline transforms, independent of how big its score is.
    /// 4. Score at or below <see cref="ComplexityWeights.SimpleThreshold"/> → <c>Simple</c>.
    /// 5. Otherwise → <c>Procedural</c>.
    /// </summary>
    private static string Classify(double score, ComplexityWeights weights, int scriptTaskOrComponentCount, int unmappedNodeCount, int sqlStatementCount, int dataFlowTaskCount)
    {
        if (scriptTaskOrComponentCount > 0 || unmappedNodeCount > 0) return "HighRisk";
        if (score >= weights.HighRiskThreshold) return "HighRisk";
        if (sqlStatementCount > 0 && dataFlowTaskCount == 0) return "SqlHeavy";
        if (score <= weights.SimpleThreshold) return "Simple";
        return "Procedural";
    }
}
