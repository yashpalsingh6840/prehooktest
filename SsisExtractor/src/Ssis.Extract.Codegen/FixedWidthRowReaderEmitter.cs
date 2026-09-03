using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits a static reader delegate companion for a fixed-width/RaggedRight Flat File Source's row
/// type, e.g. Package_Legacy.Csv.DFT_FixedWidthImportCsvRowReader -- the positional-read mirror
/// of <see cref="ClassMapEmitter"/>, used instead of it whenever
/// <see cref="ConnectionManagerSpec.FlatFileFormat"/> has no column-NAME header
/// (<see cref="FlatFileRuntimeShape.IsFixedWidthWithoutHeader"/>). CsvHelper's own <c>.Name(...)</c>
/// mapping (what <see cref="ClassMapEmitter"/> emits) cannot work here at all -- a genuinely
/// fixed-width line has no delimiters for it to split on -- so this reads
/// <see cref="Etl.Core.Csv.FixedWidthRowSource{TRow}"/>'s own <c>string[] fields</c> by ORDINAL
/// POSITION instead, one property per <see cref="ConnectionManagerSpec.FlatFileFormat"/> column,
/// in that list's own order (the file's true physical layout).
///
/// Every evidenced fixed-width column is a plain string (DT_WSTR/DT_STR, the type a raw
/// character slice naturally is) -- confirmed against RBC_Demo_ETL's own CustomersFixed.txt via a
/// real dtexec run, which lands each column padded/verbatim, never converted. A non-string
/// column is a named gap, never guessed, since there is no evidenced conversion rule for one.
/// </summary>
public static class FixedWidthRowReaderEmitter
{
    public static EmitResult Emit(string ns, string rowClassName, ConnectionManagerSpec connectionManager)
    {
        var format = connectionManager.FlatFileFormat;
        if (format is null)
        {
            return new EmitResult([], [new GenerationGap(rowClassName,
                $"connection manager '{connectionManager.ObjectName}' has no FlatFileFormat (CreationName='{connectionManager.CreationName}')")]);
        }

        var gaps = new List<GenerationGap>();
        var assignments = new List<string>();

        for (var ordinal = 0; ordinal < format.Columns.Count; ordinal++)
        {
            var column = format.Columns[ordinal];
            var type = SsisPipelineTypeMap.Resolve(CsvRowEmitter.ToPipelineTypeKey(column.DataTypeName));
            if (type is null)
            {
                // Already reported by CsvRowEmitter against the same row type -- don't double-report.
                continue;
            }

            if (type.ClrTypeName != "string")
            {
                gaps.Add(new GenerationGap($"{rowClassName}.{column.ObjectName}",
                    $"fixed-width column '{column.ObjectName}' resolves to CLR type '{type.ClrTypeName}', not string -- no fixed-width numeric/date conversion is evidenced yet, not supported"));
                continue;
            }

            assignments.Add($"        {column.ObjectName} = fields[{ordinal}],");
        }

        if (assignments.Count == 0)
        {
            gaps.Add(new GenerationGap($"{rowClassName}Reader", "no columns to read -- see this row type's own CsvRowEmitter gaps"));
            return new EmitResult([], gaps);
        }

        var lines = new List<string>
        {
            $"namespace {ns};",
            "",
            $"public static class {rowClassName}Reader",
            "{",
            $"    public static {rowClassName} Read(string[] fields) => new()",
            "    {",
        };
        lines.AddRange(assignments);
        lines.Add("    };");
        lines.Add("}");

        return new EmitResult([new GeneratedFile($"Csv/{rowClassName}Reader.cs", Rendering.JoinLines(lines))], gaps);
    }
}
