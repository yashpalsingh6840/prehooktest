namespace Ssis.Extract.Codegen;

/// <summary>
/// Minimal indent-aware line emitter, built for <see cref="PackageClassEmitter"/> (the emitter
/// rewrite, see <c>Docs/Emitter-Rewrite-Plan.md</c>) so per-method emission doesn't hand-track a
/// "    " indent string at every call site the way the old flat-script <see cref="ProgramEmitter"/>
/// did. Every <see cref="Line"/> is indented at the writer's CURRENT level; <see cref="OpenBlock"/>/
/// <see cref="CloseBlock"/> write an Allman-style "{"/"}" pair (matching this project's own
/// generated-code convention everywhere else) and step the level; <see cref="Indent"/>/
/// <see cref="Dedent"/> are the bare primitive for a construct that needs indentation without a
/// brace pair (an array initializer's own element list, e.g.).
/// </summary>
public sealed class CodeWriter
{
    private readonly List<string> _lines = [];
    private int _indent;

    public void Line(string text = "") =>
        _lines.Add(text.Length == 0 ? "" : new string(' ', _indent * 4) + text);

    public void Blank() => _lines.Add("");

    /// <summary>Appends an ALREADY-INDENTED line verbatim, bypassing this writer's own indent
    /// level -- for splicing one writer's finished, self-contained output (e.g. a container
    /// method's own body, built at its own indent level) into another.</summary>
    public void RawLine(string alreadyFormattedLine) => _lines.Add(alreadyFormattedLine);

    public void Indent() => _indent++;
    public void Dedent() => _indent--;

    /// <summary>Writes <paramref name="header"/>, then an opening brace on its own line, then
    /// increases the indent level for everything written until the matching <see cref="CloseBlock"/>.</summary>
    public void OpenBlock(string header)
    {
        Line(header);
        Line("{");
        _indent++;
    }

    /// <summary>Decreases the indent level, then writes <paramref name="closingText"/> (default
    /// "}") -- pass e.g. "});" to close a block that is itself one argument of an enclosing call.</summary>
    public void CloseBlock(string closingText = "}")
    {
        _indent--;
        Line(closingText);
    }

    public IReadOnlyList<string> Lines => _lines;

    public string Render() => Rendering.JoinLines(_lines);
}
