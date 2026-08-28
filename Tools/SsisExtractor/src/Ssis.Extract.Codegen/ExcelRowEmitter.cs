using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits an Excel Source's pipeline buffer row shape, e.g. LoadX.Excel.DripRow -- the Excel-source
/// mirror of <see cref="SqlRowEmitter"/> (same <see cref="PipelineResolver.Resolve(PipelineOutputSpec)"/>
/// resolution; kept as its own file rather than reused directly since its row type lands in its own
/// "Excel/" folder + namespace, matching the Csv/Sql per-source-kind convention, and since
/// <see cref="ExcelRowReaderEmitter"/> reads by ordinal POSITION rather than by name -- see that
/// type's own doc comment for why -- so this type's property declaration order must be preserved
/// exactly as resolved, unlike <see cref="SqlRowEmitter"/> which has no such constraint).
///
/// Property names follow the buffer's own output column name (<see cref="ResolvedColumn.PipelineColumnName"/>),
/// same as every other row-type emitter in this project.
/// </summary>
public static class ExcelRowEmitter
{
    public static EmitResult Emit(string ns, string rowClassName, PipelineComponentSpec excelSource)
    {
        var output = excelSource.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        if (output is null)
        {
            return new EmitResult([], [new GenerationGap(rowClassName, $"Excel Source '{excelSource.Name}' has no main (non-error) output")]);
        }

        var resolved = PipelineResolver.Resolve(output);
        var gaps = resolved.Unresolved
            .Select(u => new GenerationGap($"{rowClassName}.{u.ColumnName}", u.Reason))
            .ToList();

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
            var initializer = column.Type.ClrTypeName == "string" ? " = \"\";" : "";
            propertyLines.Add($"    public {column.Type.ClrTypeName} {column.PipelineColumnName} {{ get; set; }}{initializer}");
        }

        if (propertyLines.Count == 0)
        {
            if (gaps.Count == 0) gaps.Add(new GenerationGap(rowClassName, "no columns could be generated"));
            return new EmitResult([], gaps);
        }

        var lines = new List<string> { $"namespace {ns};", "", $"public sealed class {rowClassName}", "{" };
        lines.AddRange(propertyLines);
        lines.Add("}");

        return new EmitResult([new GeneratedFile($"Excel/{rowClassName}.cs", Rendering.JoinLines(lines))], gaps);
    }
}
