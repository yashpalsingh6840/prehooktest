using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Cli.Rendering;

/// <summary>
/// Turns a <see cref="PipelineSpec"/>/<see cref="LineageSpec"/> pair into a renderer-agnostic
/// node/edge graph, matching the plan's own §5.1 example diagram shape: one subgraph per
/// component, but only the columns that are actually meaningful to look at --
/// <b>every non-error output column</b> (where values are produced), plus <b>every input
/// column of a "sink" component</b> (one with no real outputs of its own, e.g. an OLE DB
/// Destination) so the final landed columns get their own terminal nodes. A plain
/// passthrough input on a non-sink transform (e.g. Derived Column's own untouched
/// <c>EmployeeID</c> input) deliberately gets no node -- the edge that matters is the one
/// straight from its true producer to wherever it's ultimately consumed, which
/// <see cref="LineageEdgeSpec"/> already expresses directly (see <see cref="LineageSpec"/>'s
/// doc comment on why lineageId matching bypasses intermediate synchronous passthroughs).
/// An edge is only rendered when both its endpoints resolved to a node under this rule --
/// which is exactly what filters "PathFlow" edges down to the ones ending at a sink.
/// </summary>
internal static class LineageGraphModel
{
    public sealed class Node
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
        public required string ComponentRefId { get; init; }
        public required string ComponentName { get; init; }
        public bool IsConstant { get; set; }
    }

    public sealed class Edge
    {
        public required string FromId { get; init; }
        public required string ToId { get; init; }
        public string? Label { get; init; }
    }

    public sealed class Graph
    {
        public List<(string ComponentRefId, string ComponentName, List<Node> Nodes)> Subgraphs { get; init; } = [];
        public List<Edge> Edges { get; init; } = [];
    }

    public static Graph Build(PipelineSpec pipeline, LineageSpec lineage)
    {
        var nodesByColumnRefId = new Dictionary<string, Node>();
        var subgraphs = new List<(string, string, List<Node>)>();

        foreach (var comp in pipeline.Components)
        {
            var compNodes = new List<Node>();
            var isSink = comp.Outputs.Count == 0 || comp.Outputs.All(o => o.IsErrorOut == true);

            foreach (var output in comp.Outputs.Where(o => o.IsErrorOut != true))
            {
                foreach (var col in output.Columns)
                {
                    var node = new Node
                    {
                        Id = col.RefId,
                        Label = FormatLabel(col.Name, col.DataType, col.Length),
                        ComponentRefId = comp.RefId,
                        ComponentName = comp.Name,
                    };
                    compNodes.Add(node);
                    nodesByColumnRefId[col.RefId] = node;
                }
            }

            if (isSink)
            {
                foreach (var input in comp.Inputs)
                {
                    foreach (var col in input.Columns)
                    {
                        var node = new Node
                        {
                            Id = col.RefId,
                            Label = FormatLabel(col.CachedName, col.CachedDataType, col.CachedLength),
                            ComponentRefId = comp.RefId,
                            ComponentName = comp.Name,
                        };
                        compNodes.Add(node);
                        nodesByColumnRefId[col.RefId] = node;
                    }
                }
            }

            if (compNodes.Count > 0)
            {
                subgraphs.Add((comp.RefId, comp.Name, compNodes));
            }
        }

        foreach (var constant in lineage.ConstantColumns)
        {
            if (nodesByColumnRefId.TryGetValue(constant.ColumnRefId, out var node))
            {
                node.IsConstant = true;
            }
        }

        var edges = new List<Edge>();
        foreach (var e in lineage.Edges)
        {
            if (!nodesByColumnRefId.ContainsKey(e.FromColumnRefId) || !nodesByColumnRefId.ContainsKey(e.ToColumnRefId))
            {
                continue; // filtered out per this type's doc comment (passthrough-only hop, or an unresolved producer)
            }

            edges.Add(new Edge
            {
                FromId = e.FromColumnRefId,
                ToId = e.ToColumnRefId,
                Label = e.Kind == "ExpressionDerived" ? Truncate(e.Expression, 60) : null,
            });
        }

        return new Graph
        {
            Subgraphs = subgraphs.Select(s => (s.Item1, s.Item2, s.Item3)).ToList(),
            Edges = edges,
        };
    }

    private static string FormatLabel(string name, string? dataType, int? length) =>
        dataType is null ? name : length is null ? $"{name} ({dataType})" : $"{name} ({dataType},{length})";

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max] + "...";
}
