using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits the CsvHelper ClassMap that goes with <see cref="CsvRowEmitter"/>'s row type, e.g.
/// LoadEmployees.Csv.EmployeeCsvRowMap. Deliberately does NOT emit
/// <c>.TypeConverterOption.Format(...)</c> for date columns the way the hand-written
/// EmployeeCsvRowMap does -- no per-column date format string exists anywhere in a .dtsx
/// (FlatFileColumnSpec carries ColumnType/DataType/MaximumWidth/precision/scale and nothing
/// else), so CsvHelper's own invariant-culture default parsing is relied on instead of
/// inventing a format that was never actually in the source package.
/// </summary>
public static class ClassMapEmitter
{
    public static EmitResult Emit(string ns, string rowClassName, ConnectionManagerSpec connectionManager)
    {
        var mapClassName = $"{rowClassName}Map";
        var format = connectionManager.FlatFileFormat;
        if (format is null)
        {
            return new EmitResult([], [new GenerationGap(mapClassName,
                $"connection manager '{connectionManager.ObjectName}' has no FlatFileFormat (CreationName='{connectionManager.CreationName}')")]);
        }

        // Independently reproduces CsvRowEmitter's own identifier mapping (same format.Columns
        // list, same order) -- see PackageGenerator.MakeColumnIdentifierResolver's own doc
        // comment.
        var identifierOf = PackageGenerator.MakeColumnIdentifierResolver();

        var gaps = new List<GenerationGap>();
        var mapLines = new List<string>();

        foreach (var column in format.Columns)
        {
            // Mirrors CsvRowEmitter's own gate exactly: a column CsvRowEmitter skipped has no
            // property on the row class, so mapping it here wouldn't compile. Kept as a
            // duplicated three-line check rather than threading a shared "resolved columns"
            // type through both emitters for this alone.
            if (SsisPipelineTypeMap.Resolve(CsvRowEmitter.ToPipelineTypeKey(column.DataTypeName)) is null)
            {
                gaps.Add(new GenerationGap($"{mapClassName}.{column.ObjectName}", $"unmapped flat-file data type '{column.DataTypeName}'"));
                continue;
            }

            // .Name(...) is the real CSV HEADER text -- a literal CsvHelper matches against, must
            // stay the raw column name verbatim (never sanitized). Only the m.{...} property
            // reference (identifier position) routes through the sanitized identifier.
            mapLines.Add($"        Map(m => m.{identifierOf(column.ObjectName)}).Name(\"{column.ObjectName}\");");
        }

        if (mapLines.Count == 0)
        {
            if (gaps.Count == 0) gaps.Add(new GenerationGap(mapClassName, "no columns could be mapped"));
            return new EmitResult([], gaps);
        }

        var lines = new List<string>
        {
            "using CsvHelper.Configuration;",
            "",
            $"namespace {ns};",
            "",
            $"public sealed class {mapClassName} : ClassMap<{rowClassName}>",
            "{",
            $"    public {mapClassName}()",
            "    {",
        };
        lines.AddRange(mapLines);
        lines.Add("    }");
        lines.Add("}");

        return new EmitResult([new GeneratedFile($"Csv/{mapClassName}.cs", Rendering.JoinLines(lines))], gaps);
    }
}
