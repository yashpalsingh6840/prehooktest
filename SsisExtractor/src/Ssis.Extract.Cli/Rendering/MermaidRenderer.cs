using System.Text;

namespace Ssis.Extract.Cli.Rendering;

/// <summary>Renders a <see cref="LineageGraphModel.Graph"/> as a Mermaid flowchart (plan §5.1) -- the same shape as the plan's own example diagram: one subgraph per component, columns as nodes, constant columns highlighted, no incoming edge.</summary>
internal static class MermaidRenderer
{
    public static string Render(LineageGraphModel.Graph graph, string title)
    {
        var ids = AssignIds(graph);
        var sb = new StringBuilder();
        sb.AppendLine("flowchart LR");
        sb.AppendLine($"    %% {EscapeComment(title)}");

        var sgIndex = 0;
        foreach (var (componentRefId, componentName, nodes) in graph.Subgraphs)
        {
            sb.AppendLine($"    subgraph sg{sgIndex}[\"{EscapeLabel(componentName)}\"]");
            foreach (var node in nodes)
            {
                sb.AppendLine($"        {ids[node.Id]}[\"{EscapeLabel(node.Label)}\"]");
            }
            sb.AppendLine("    end");
            sgIndex++;
        }

        foreach (var edge in graph.Edges)
        {
            if (edge.Label is null)
            {
                sb.AppendLine($"    {ids[edge.FromId]} --> {ids[edge.ToId]}");
            }
            else
            {
                sb.AppendLine($"    {ids[edge.FromId]} -->|\"{EscapeLabel(edge.Label)}\"| {ids[edge.ToId]}");
            }
        }

        foreach (var (_, _, nodes) in graph.Subgraphs)
        {
            foreach (var node in nodes.Where(n => n.IsConstant))
            {
                sb.AppendLine($"    style {ids[node.Id]} fill:#8a5a1a,color:#fff");
            }
        }

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

    private static string EscapeLabel(string s) => s.Replace("\"", "'");
    private static string EscapeComment(string s) => s.Replace("\r", " ").Replace("\n", " ");
}
