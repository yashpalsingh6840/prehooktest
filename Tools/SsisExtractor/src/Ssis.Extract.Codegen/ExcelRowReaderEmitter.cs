using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits a static reader delegate companion for an Excel Source's row type, e.g.
/// LoadX.Excel.DripRowReader -- the Excel-source mirror of <see cref="SqlRowReaderEmitter"/>.
///
/// <b>Reads by ordinal POSITION, never by name</b> -- confirmed real (not assumed) by probing
/// <c>ExcelDataReader</c>'s own raw <c>IExcelDataReader</c> (the API used with no <c>.AsDataSet()</c>
/// binding, i.e. no header-aware DataTable): <c>GetName(i)</c> throws
/// <c>NotSupportedException</c> unconditionally, and there is no working <c>GetOrdinal(name)</c>
/// either, since the reader never parses header text into column identity at all -- it is a plain
/// positional grid. This is the same constraint a Flat File Source already has (columns identified
/// by position in the row, not name), unlike a SQL SELECT's own named result columns
/// (<see cref="SqlRowReaderEmitter"/>'s own <c>reader.GetOrdinal("name")</c>). The ordinal used
/// here is each column's index within <see cref="PipelineResolver.Resolve(PipelineOutputSpec)"/>'s
/// own <c>Columns</c> list -- computed BEFORE any null-typed column is skipped, so a later
/// column's true worksheet position is never shifted by an earlier one being unsupported.
///
/// Uses a typed <c>IDataRecord</c> accessor per CLR type (<c>GetDouble</c>/<c>GetString</c>/...)
/// rather than <see cref="SqlRowReaderEmitter"/>'s uniform <c>GetFieldValue&lt;T&gt;</c>, since
/// <c>IExcelDataReader</c> implements the plain <c>System.Data.IDataRecord</c> interface, not
/// <c>System.Data.Common.DbDataReader</c> -- <c>GetFieldValue&lt;T&gt;</c> is a <c>DbDataReader</c>-only
/// convenience method with no <c>IDataRecord</c> equivalent.
/// </summary>
public static class ExcelRowReaderEmitter
{
    /// <summary>ClrTypeName (from <see cref="Ssis.Extract.Model.Shared.SsisPipelineTypeMap"/>) ->
    /// the <c>IDataRecord</c> method that reads it. Only types with a real <c>IDataRecord</c>
    /// accessor are listed -- anything else (DateOnly, TimeOnly, Guid, byte[], ...) is a named
    /// gap, never guessed, since <c>IDataRecord</c> has no generic equivalent to fall back on.</summary>
    private static readonly Dictionary<string, string> AccessorByClrType = new(StringComparer.Ordinal)
    {
        ["string"] = "GetString",
        ["double"] = "GetDouble",
        ["float"] = "GetFloat",
        ["int"] = "GetInt32",
        ["long"] = "GetInt64",
        ["short"] = "GetInt16",
        ["bool"] = "GetBoolean",
        ["decimal"] = "GetDecimal",
        ["DateTime"] = "GetDateTime",
    };

    public static EmitResult Emit(string ns, string rowClassName, PipelineComponentSpec excelSource)
    {
        var output = excelSource.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        if (output is null)
        {
            return new EmitResult([], [new GenerationGap(rowClassName, $"Excel Source '{excelSource.Name}' has no main (non-error) output")]);
        }

        var resolved = PipelineResolver.Resolve(output);

        // Independently reproduces ExcelRowEmitter's own identifier mapping -- see
        // PackageGenerator.MakeColumnIdentifierResolver's own doc comment.
        var identifierOf = PackageGenerator.MakeColumnIdentifierResolver();

        var assignments = new List<string>();
        var gaps = new List<GenerationGap>();
        for (var ordinal = 0; ordinal < resolved.Columns.Count; ordinal++)
        {
            var column = resolved.Columns[ordinal];
            // Unmapped-type/unresolved columns are already reported by ExcelRowEmitter against
            // the same row type -- don't double-report the same fact from a second emitter.
            if (column.Type is null) continue;

            if (!AccessorByClrType.TryGetValue(column.Type.ClrTypeName, out var accessor))
            {
                gaps.Add(new GenerationGap($"{rowClassName}.{column.PipelineColumnName}",
                    $"Excel Source column of CLR type '{column.Type.ClrTypeName}' has no IDataRecord accessor to read it with -- not supported yet"));
                continue;
            }

            assignments.Add($"        {identifierOf(column.PipelineColumnName)} = reader.{accessor}({ordinal}),");
        }

        if (assignments.Count == 0)
        {
            gaps.Add(new GenerationGap($"{rowClassName}Reader", "no columns to read -- see this row type's own ExcelRowEmitter gaps"));
            return new EmitResult([], gaps);
        }

        var lines = new List<string>
        {
            "using ExcelDataReader;",
            "",
            $"namespace {ns};",
            "",
            $"public static class {rowClassName}Reader",
            "{",
            $"    public static {rowClassName} Read(IExcelDataReader reader) => new()",
            "    {",
        };
        lines.AddRange(assignments);
        lines.Add("    };");
        lines.Add("}");

        return new EmitResult([new GeneratedFile($"Excel/{rowClassName}Reader.cs", Rendering.JoinLines(lines))], gaps);
    }
}
