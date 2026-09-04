using System.Text;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Pipeline;

namespace Svk.Core;

/// <summary>
/// Renders one Data Flow Task's own component wiring -- which component feeds which -- as a
/// Mermaid <c>flowchart TD</c> block, using <see cref="PipelineFlowOrder"/>'s physical-path-based
/// edges (not column lineage, which by design skips straight past a passthrough component like
/// Multicast to its true producer, leaving it floating with no arrows at all). Node labels are
/// numbered by the same level the per-component table sorts by, so a diagram node and its
/// matching table row(s) always carry the identical number -- and two components with no
/// dependency on each other (e.g. two branches fanned out from the same Multicast) correctly
/// share one number rather than being given an arbitrary, misleading order relative to each other.
/// </summary>
public static class DataFlowDiagramBuilder
{
    /// <summary>Null when there's nothing worth diagramming (0 or 1 components).</summary>
    public static string? BuildMermaid(PipelineSpec pipeline)
    {
        if (pipeline.Components.Count < 2) return null;

        var flow = PipelineFlowOrder.Compute(pipeline);
        var byRefId = pipeline.Components.ToDictionary(c => c.RefId);

        var sb = new StringBuilder();
        sb.AppendLine("flowchart TD");

        foreach (var refId in flow.OrderedComponentRefIds)
        {
            var comp = byRefId[refId];
            var id = MermaidUtil.SanitizeId(comp.RefId);
            var name = MermaidUtil.EscapeLabel(comp.Name, max: 40);
            var type = MermaidUtil.EscapeLabel(MermaidUtil.FriendlyType(comp.ComponentClassId), max: 30);
            sb.AppendLine($"    {id}[\"{flow.LevelByRefId[refId]}. {name}<br/><small>{type}</small>\"]");
        }

        foreach (var e in flow.Edges)
        {
            var fromId = MermaidUtil.SanitizeId(e.FromRefId);
            var toId = MermaidUtil.SanitizeId(e.ToRefId);
            var label = e.ColumnCount == 1 ? "1 col" : $"{e.ColumnCount} cols";
            sb.AppendLine($"    {fromId} -->|\"{label}\"| {toId}");
        }

        return sb.ToString();
    }
}
