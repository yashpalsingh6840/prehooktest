using System.Text;

namespace Ssis.Extract.Cli.Rendering;

/// <summary>Renders a <see cref="LineageGraphModel.Graph"/> as Graphviz DOT (plan §5.1) -- same node/edge set as <see cref="MermaidRenderer"/>, different syntax.</summary>
internal static class DotRenderer
{
    public static string Render(LineageGraphModel.Graph graph, string title)
    {
        var ids = AssignIds(graph);
        var sb = new StringBuilder();
        sb.AppendLine("digraph Lineage {");
        sb.AppendLine($"  // {EscapeComment(title)}");
        sb.AppendLine("  rankdir=LR;");
        sb.AppendLine("  node [shape=box];");

        var sgIndex = 0;
        foreach (var (componentRefId, componentName, nodes) in graph.Subgraphs)
        {
            sb.AppendLine($"  subgraph cluster_{sgIndex} {{");
            sb.AppendLine($"    label=\"{EscapeLabel(componentName)}\";");
            foreach (var node in nodes)
            {
                var style = node.IsConstant ? ", style=filled, fillcolor=\"#8a5a1a\", fontcolor=\"#ffffff\"" : "";
                sb.AppendLine($"    {ids[node.Id]} [label=\"{EscapeLabel(node.Label)}\"{style}];");
            }
            sb.AppendLine("  }");
            sgIndex++;
        }

        foreach (var edge in graph.Edges)
        {
            var label = edge.Label is null ? "" : $" [label=\"{EscapeLabel(edge.Label)}\"]";
            sb.AppendLine($"  {ids[edge.FromId]} -> {ids[edge.ToId]}{label};");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static Dictionary<string, string> AssignIds(LineageGraphModel.Graph graph)
    {
        var ids = new Dictionary<string, string>();
        var n = 0;
        foreach (var (_, _, nodes) in graph.Subgraphs)
        {
            foreach (var node in nodes)
            {
                ids[node.Id] = $"n{n++}";
            }
        }
        return ids;
    }

    private static string EscapeLabel(string s) => s.Replace("\\", "\\\\").Replace("\"", "'");
    private static string EscapeComment(string s) => s.Replace("\r", " ").Replace("\n", " ");
}
