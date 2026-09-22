using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits an XML Source's pipeline buffer row shape, e.g. LoadX.Xml.RecordRow -- the XML-source
/// mirror of <see cref="SqlRowEmitter"/>/<see cref="ExcelRowEmitter"/> (same
/// <see cref="PipelineResolver.Resolve(PipelineOutputSpec)"/> resolution; kept as its own file
/// rather than reused directly since its row type lands in its own "Xml/" folder + namespace,
/// matching the Csv/Sql/Excel per-source-kind convention).
///
/// <b>Every string-typed column is nullable (<c>string?</c>, no <c>= ""</c> initializer)</b> --
/// unlike every other row-type emitter in this project, which defaults a string property to
/// empty. This is a MEASURED, not guessed, difference: a real dtexec run of a dedicated fixture
/// (<c>SyntheticXmlSource.dtsx</c>) showed BOTH an empty XML element
/// (<c>&lt;email&gt;&lt;/email&gt;</c>) AND an entirely OMITTED optional element (no
/// <c>&lt;gender&gt;</c> at all) resolve to a genuine NULL at the destination, never an empty
/// string -- see <see cref="XmlRowReaderEmitter"/>'s own doc comment for the read-side half of
/// this. A non-string column's own missing-element behavior was not measured (the one evidenced
/// real instance always populates its numeric <c>id</c> column), so it is left non-nullable.
///
/// Property names follow the buffer's own output column name (<see cref="ResolvedColumn.PipelineColumnName"/>),
/// same as every other row-type emitter in this project.
/// </summary>
public static class XmlRowEmitter
{
    public static EmitResult Emit(string ns, string rowClassName, PipelineComponentSpec xmlSource)
    {
        var output = xmlSource.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        if (output is null)
        {
            return new EmitResult([], [new GenerationGap(rowClassName, $"XML Source '{xmlSource.Name}' has no main (non-error) output")]);
        }

        var resolved = PipelineResolver.Resolve(output);
        var gaps = resolved.Unresolved
            .Select(u => new GenerationGap($"{rowClassName}.{u.ColumnName}", u.Reason))
            .ToList();

        // XmlRowReaderEmitter independently reproduces this exact mapping (same input list,
        // same order) -- see PackageGenerator.MakeColumnIdentifierResolver's own doc comment.
        var identifierOf = PackageGenerator.MakeColumnIdentifierResolver();

        var propertyLines = new List<string>();
        foreach (var column in resolved.Columns)
        {
            if (column.Type is null)
            {
                gaps.Add(new GenerationGap($"{rowClassName}.{column.PipelineColumnName}",
                    $"unmapped pipeline data type '{column.ExternalDataType}' -- add it to SsisPipelineTypeMap before this column can be generated"));
                continue;
            }

            if (propertyLines.Count > 0) propertyLines.Add("");
            var isString = column.Type.ClrTypeName == "string";
            var typeName = isString ? "string?" : column.Type.ClrTypeName;
            propertyLines.Add($"    public {typeName} {identifierOf(column.PipelineColumnName)} {{ get; set; }}");
        }

        if (propertyLines.Count == 0)
        {
            if (gaps.Count == 0) gaps.Add(new GenerationGap(rowClassName, "no columns could be generated"));
            return new EmitResult([], gaps);
        }

        var lines = new List<string> { $"namespace {ns};", "", $"public sealed class {rowClassName}", "{" };
        lines.AddRange(propertyLines);
        lines.Add("}");

        return new EmitResult([new GeneratedFile($"Xml/{rowClassName}.cs", Rendering.JoinLines(lines))], gaps);
    }
}
