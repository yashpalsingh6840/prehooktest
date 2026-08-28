using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits an OLE DB Source's pipeline buffer row shape, e.g. LoadX.Sql.OrderRow -- the read-side
/// mirror of <see cref="EntityEmitter"/> (which resolves a DESTINATION's input; this resolves a
/// SOURCE's own main output). Source of truth is <see cref="PipelineResolver.Resolve(PipelineOutputSpec)"/>.
///
/// Property names follow the buffer's own output column name
/// (<see cref="ResolvedColumn.PipelineColumnName"/>), not the source table's column name -- this
/// is what <see cref="TransformEmitter"/>'s <c>row.ColumnName</c> passthrough references
/// downstream, and what <see cref="SqlRowReaderEmitter"/>'s ordinal lookup must match.
/// </summary>
public static class SqlRowEmitter
{
    public static EmitResult Emit(string ns, string rowClassName, PipelineComponentSpec oleDbSource, IReadOnlySet<string>? nullableColumnNames = null)
    {
        var output = oleDbSource.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        if (output is null)
        {
            return new EmitResult([], [new GenerationGap(rowClassName, $"OLE DB Source '{oleDbSource.Name}' has no main (non-error) output")]);
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
            // NullabilityInference's own evidence (an ISNULL(x) check on this column somewhere
            // downstream) -- see EntityEmitter's identical note for why string is excluded
            // (a reference type needs no type-level change to hold null).
            var isNullable = column.Type.ClrTypeName != "string" && (nullableColumnNames?.Contains(column.PipelineColumnName) ?? false);
            var typeName = isNullable ? column.Type.ClrTypeName + "?" : column.Type.ClrTypeName;
            var initializer = !isNullable && column.Type.ClrTypeName == "string" ? " = \"\";" : "";
            propertyLines.Add($"    public {typeName} {column.PipelineColumnName} {{ get; set; }}{initializer}");
        }

        if (propertyLines.Count == 0)
        {
            if (gaps.Count == 0) gaps.Add(new GenerationGap(rowClassName, "no columns could be generated"));
            return new EmitResult([], gaps);
        }

        var lines = new List<string> { $"namespace {ns};", "", $"public sealed class {rowClassName}", "{" };
        lines.AddRange(propertyLines);
        lines.Add("}");

        return new EmitResult([new GeneratedFile($"Sql/{rowClassName}.cs", Rendering.JoinLines(lines))], gaps);
    }
}
