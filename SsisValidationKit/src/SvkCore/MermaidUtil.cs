using System.Text;

namespace Svk.Core;

/// <summary>Shared helpers for the Mermaid <c>flowchart</c> blocks emitted by
/// <see cref="ControlFlowDiagramBuilder"/> and <see cref="DataFlowDiagramBuilder"/>.</summary>
internal static class MermaidUtil
{
    /// <summary>A RefId can contain characters Mermaid rejects in a bare node id (colons, braces,
    /// backslashes). Deterministic and collision-safe within one diagram since it's a 1:1
    /// character substitution, not a hash.</summary>
    public static string SanitizeId(string refId)
    {
        var sb = new StringBuilder("n_", refId.Length + 2);
        foreach (var c in refId)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }
        return sb.ToString();
    }

    /// <summary>Escapes a node/edge label for Mermaid's quoted-string form -- ["..."] / |"..."| --
    /// and truncates so one long expression can't blow out the diagram's layout.</summary>
    public static string EscapeLabel(string? s, int max = 60)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var truncated = s.Length > max ? s[..max] + "…" : s;
        return truncated.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
    }

    /// <summary>Strips the near-universal "Microsoft." prefix so a node subtitle reads
    /// "ConditionalSplit" instead of "Microsoft.ConditionalSplit" -- purely cosmetic.</summary>
    public static string FriendlyType(string type) =>
        type.StartsWith("Microsoft.", StringComparison.Ordinal) ? type["Microsoft.".Length..] : type;
}
