using System.Text;
using Ssis.Extract.Model.Package;

namespace Svk.Core;

/// <summary>
/// Renders one container's control flow -- its direct children plus the
/// <see cref="PrecedenceConstraintSpec"/> edges ordering them -- as a Mermaid
/// <c>flowchart TD</c> block. This is the "who calls who" picture: which task runs after
/// which, and under what real condition, not just the flattened top-to-bottom heading list
/// <see cref="ExecutionOrderBuilder"/> produces on its own.
///
/// A node with no incoming/outgoing constraint at all still gets declared (just with no
/// edges attached) -- Mermaid renders it as an unconnected box, which is exactly the visual
/// for "this ran independently, in parallel with whatever else has no path to it either" --
/// a stronger signal than <see cref="ExecutionOrderBuilder"/>'s own text-only parallel callout.
/// </summary>
public static class ControlFlowDiagramBuilder
{
    /// <summary>Null when there's nothing worth diagramming (0 or 1 children -- a single node has no "who calls who" to show).</summary>
    public static string? BuildMermaid(List<ExecutableSpec> children, List<PrecedenceConstraintSpec> constraints)
    {
        if (children.Count < 2) return null;

        var sb = new StringBuilder();
        sb.AppendLine("flowchart TD");

        var disabledIds = new List<string>();
        foreach (var ex in children)
        {
            var id = MermaidUtil.SanitizeId(ex.RefId);
            var name = MermaidUtil.EscapeLabel(ex.ObjectName ?? ex.RefId, max: 40);
            var type = MermaidUtil.EscapeLabel(MermaidUtil.FriendlyType(ex.ExecutableType), max: 30);
            var disabledSuffix = ex.Disabled == true ? " (disabled)" : "";
            sb.AppendLine($"    {id}[\"{name}<br/><small>{type}{disabledSuffix}</small>\"]");
            if (ex.Disabled == true) disabledIds.Add(id);
        }

        foreach (var c in constraints)
        {
            var fromId = MermaidUtil.SanitizeId(c.From);
            var toId = MermaidUtil.SanitizeId(c.To);
            var label = BuildConstraintLabel(c);
            sb.AppendLine(label.Length == 0
                ? $"    {fromId} --> {toId}"
                : $"    {fromId} -->|\"{label}\"| {toId}");
        }

        if (disabledIds.Count > 0)
        {
            sb.AppendLine("    classDef disabled stroke-dasharray: 4 4,opacity:0.55;");
            sb.AppendLine($"    class {string.Join(",", disabledIds)} disabled;");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Both <see cref="PrecedenceConstraintSpec.Value"/> and <see cref="PrecedenceConstraintSpec.EvalOp"/>
    /// are persisted as raw SSIS enum integer codes, not word names -- confirmed against the real
    /// XML (<c>DTS:Value="1"</c>, <c>DTS:EvalOp="3"</c>), not assumed from the model's own
    /// paraphrased doc comment. Mapping is the one this project already reflected out of the real
    /// GAC enums (see CLAUDE.md's conditional-precedence-constraint round): <c>DTSExecResult</c>
    /// Success=0/Failure=1/Completion=2; <c>DTSPrecedenceEvalOp</c> Expression=1/Constraint=2/
    /// ExpressionAndConstraint=3/ExpressionOrConstraint=4.
    ///
    /// Empty string means an unconditional "runs after predecessor succeeds" edge (Value absent
    /// or "0", EvalOp absent or "2"), which needs no label. Otherwise renders the real semantics,
    /// including the non-obvious one this project measured against real dtexec: an EvalOp="1"
    /// (Expression-only) constraint ignores the predecessor's outcome entirely -- it runs on
    /// success OR failure, gated purely by the expression -- so that shape is labeled
    /// "(any outcome)" rather than left to look like an ordinary success-gated edge.
    /// </summary>
    private static string BuildConstraintLabel(PrecedenceConstraintSpec c)
    {
        var expr = MermaidUtil.EscapeLabel(c.Expression, max: 40);
        var hasExpr = !string.IsNullOrEmpty(expr);

        if (c.EvalOp == "1")
        {
            return hasExpr ? $"[{expr}] (any outcome)" : "(any outcome)";
        }

        var outcome = c.Value switch
        {
            "1" => "Failure",
            "2" => "Completion",
            _ => (string?)null, // null or "0" -- the schema default, Success
        };

        if (c.EvalOp is "3" or "4" && hasExpr)
        {
            var op = c.EvalOp == "4" ? "OR" : "AND";
            return $"{outcome ?? "Success"} {op} [{expr}]";
        }

        return outcome ?? "";
    }
}
