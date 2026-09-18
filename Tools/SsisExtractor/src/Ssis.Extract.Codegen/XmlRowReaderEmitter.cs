using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits a static reader delegate companion for an XML Source's row type, e.g.
/// LoadX.Xml.RecordRowReader -- the XML-source mirror of <see cref="SqlRowReaderEmitter"/>/
/// <see cref="ExcelRowReaderEmitter"/>.
///
/// <b>Reads by column NAME, not by ordinal position</b> -- a deliberate, genuine improvement
/// over <see cref="ExcelRowReaderEmitter"/>'s own positional read, not a blind copy of it. That
/// emitter's own doc comment explains its positional reading is forced by a real limitation of
/// the raw <c>IExcelDataReader</c> API (<c>GetName(i)</c> throws <c>NotSupportedException</c>
/// unconditionally, no working <c>GetOrdinal(name)</c> either). <c>System.Xml.Linq</c>'s
/// <c>XElement.Element(name)</c> has no such limitation -- an XML element's own child elements
/// are inherently named, and reading by name is both simpler and more robust (immune to the
/// row-element's children appearing in a different order than the schema declared, which SSIS's
/// own XML Source component does not require either).
///
/// Every non-string column is parsed via <c>{Type}.Parse(...)</c> against the child element's own
/// text content (culture-invariant) -- unlike <see cref="ExcelRowReaderEmitter"/>, whose source
/// already hands back typed values, XML content is always text. A column missing its own child
/// element entirely throws (via <c>!.Value</c>) rather than silently defaulting -- <b>this was
/// NOT independently measured for a non-string column</b> (the one evidenced real instance always
/// populates its numeric <c>id</c> column), stated honestly as a limitation rather than guessed.
///
/// Every STRING column instead resolves via <see cref="NullableTextHelperName"/> (emitted once
/// per reader class), collapsing BOTH a missing element and an empty one to <c>null</c> --
/// MEASURED real behaviour (see <see cref="XmlRowEmitter"/>'s own doc comment for the dtexec run
/// this came from), not a guess.
/// </summary>
public static class XmlRowReaderEmitter
{
    /// <summary>ClrTypeName (from <see cref="Ssis.Extract.Model.Shared.SsisPipelineTypeMap"/>) ->
    /// the <c>{Type}.Parse(...)</c> expression template to read a non-string column's own raw text
    /// with. Only types with an evidenced-safe, culture-invariant <c>Parse</c> overload are
    /// listed -- anything else is a named gap, never guessed.</summary>
    private static readonly Dictionary<string, Func<string, string>> ParseExpressionByClrType = new(StringComparer.Ordinal)
    {
        ["short"] = expr => $"short.Parse({expr}, System.Globalization.CultureInfo.InvariantCulture)",
        ["int"] = expr => $"int.Parse({expr}, System.Globalization.CultureInfo.InvariantCulture)",
        ["long"] = expr => $"long.Parse({expr}, System.Globalization.CultureInfo.InvariantCulture)",
        ["float"] = expr => $"float.Parse({expr}, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture)",
        ["double"] = expr => $"double.Parse({expr}, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture)",
        ["decimal"] = expr => $"decimal.Parse({expr}, System.Globalization.CultureInfo.InvariantCulture)",
        ["bool"] = expr => $"bool.Parse({expr})",
        ["DateOnly"] = expr => $"DateOnly.Parse({expr}, System.Globalization.CultureInfo.InvariantCulture)",
        ["DateTime"] = expr => $"DateTime.Parse({expr}, System.Globalization.CultureInfo.InvariantCulture)",
    };

    public static EmitResult Emit(string ns, string rowClassName, PipelineComponentSpec xmlSource)
    {
        var output = xmlSource.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        if (output is null)
        {
            return new EmitResult([], [new GenerationGap(rowClassName, $"XML Source '{xmlSource.Name}' has no main (non-error) output")]);
        }

        var resolved = PipelineResolver.Resolve(output);

        // Independently reproduces XmlRowEmitter's own identifier mapping -- see
        // PackageGenerator.MakeColumnIdentifierResolver's own doc comment.
        var identifierOf = PackageGenerator.MakeColumnIdentifierResolver();

        var assignments = new List<string>();
        var gaps = new List<GenerationGap>();
        var usesNullableText = false;
        foreach (var column in resolved.Columns)
        {
            // Unmapped-type/unresolved columns are already reported by XmlRowEmitter against the
            // same row type -- don't double-report the same fact from a second emitter.
            if (column.Type is null) continue;

            // The real XML child ELEMENT name -- a LITERAL lookup, must stay the raw name
            // verbatim (never sanitized): the source document has no idea this tool needs a C#
            // identifier for it.
            var elementAccess = $"element.Element(\"{column.PipelineColumnName}\")";
            var identifier = identifierOf(column.PipelineColumnName);
            if (column.Type.ClrTypeName == "string")
            {
                usesNullableText = true;
                assignments.Add($"        {identifier} = ReadNullableText({elementAccess}),");
                continue;
            }

            if (!ParseExpressionByClrType.TryGetValue(column.Type.ClrTypeName, out var buildExpr))
            {
                gaps.Add(new GenerationGap($"{rowClassName}.{column.PipelineColumnName}",
                    $"XML Source column of CLR type '{column.Type.ClrTypeName}' has no Parse(...) expression to read it with -- not supported yet"));
                continue;
            }

            assignments.Add($"        {identifier} = {buildExpr($"{elementAccess}!.Value")},");
        }

        if (assignments.Count == 0)
        {
            gaps.Add(new GenerationGap($"{rowClassName}Reader", "no columns to read -- see this row type's own XmlRowEmitter gaps"));
            return new EmitResult([], gaps);
        }

        var lines = new List<string>
        {
            "using System.Xml.Linq;",
            "",
            $"namespace {ns};",
            "",
            $"public static class {rowClassName}Reader",
            "{",
            $"    public static {rowClassName} Read(XElement element) => new()",
            "    {",
        };
        lines.AddRange(assignments);
        lines.Add("    };");

        if (usesNullableText)
        {
            lines.Add("");
            // Collapses BOTH a missing element (null XElement) and an empty one (Value == "")
            // to null -- the measured real behaviour, see this type's own doc comment.
            lines.Add("    private static string? ReadNullableText(XElement? element)");
            lines.Add("    {");
            lines.Add("        var value = element?.Value;");
            lines.Add("        return string.IsNullOrEmpty(value) ? null : value;");
            lines.Add("    }");
        }

        lines.Add("}");

        return new EmitResult([new GeneratedFile($"Xml/{rowClassName}Reader.cs", Rendering.JoinLines(lines))], gaps);
    }
}
