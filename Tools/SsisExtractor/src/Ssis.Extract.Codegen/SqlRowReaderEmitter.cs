using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits a static reader delegate companion for an OLE DB Source's row type, e.g.
/// LoadX.Sql.OrderRowReader -- the SQL-source mirror of <see cref="ClassMapEmitter"/> (which
/// emits a CsvHelper ClassMap instead; a DbDataReader needs no such library, just ordinal
/// lookups by name). Uses <c>reader.GetFieldValue&lt;T&gt;(reader.GetOrdinal(name))</c> per
/// column -- one uniform call regardless of type, unlike CsvHelper's per-type accessor table.
///
/// Looks columns up BY NAME using the buffer's own column name
/// (<see cref="ResolvedColumn.PipelineColumnName"/>), matching <see cref="SqlRowEmitter"/>'s own
/// property names. This is a real, load-bearing assumption about the SELECT's own column
/// names/aliases -- see <see cref="PackageGenerator"/>'s SELECT-building logic for why an
/// AccessMode=0 (OpenRowset) flow's generated SELECT is built specifically to guarantee this,
/// and why an AccessMode=2 (author's own SqlCommand) flow instead carries a non-fatal
/// GenerationGap stating this as an assumption to verify, not a fact this tool confirmed.
/// </summary>
public static class SqlRowReaderEmitter
{
    public static EmitResult Emit(string ns, string rowClassName, PipelineComponentSpec oleDbSource, IReadOnlySet<string>? nullableColumnNames = null)
    {
        var output = oleDbSource.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        if (output is null)
        {
            return new EmitResult([], [new GenerationGap(rowClassName, $"OLE DB Source '{oleDbSource.Name}' has no main (non-error) output")]);
        }

        var resolved = PipelineResolver.Resolve(output);

        // Independently reproduces SqlRowEmitter's own identifier mapping -- see
        // PackageGenerator.MakeColumnIdentifierResolver's own doc comment for why this needs no
        // state shared with that emitter.
        var identifierOf = PackageGenerator.MakeColumnIdentifierResolver();

        var assignments = new List<string>();
        foreach (var column in resolved.Columns)
        {
            // Unmapped-type/unresolved columns are already reported by SqlRowEmitter against
            // the same row type -- don't double-report the same fact from a second emitter.
            if (column.Type is null) continue;

            // The real SQL SELECT's own column name/alias -- a LITERAL lookup, must stay the raw
            // name verbatim (never sanitized): the actual result set has no idea this tool needs
            // a C# identifier for it.
            var ordinalExpr = $"reader.GetOrdinal(\"{column.PipelineColumnName}\")";
            var readExpr = $"reader.GetFieldValue<{column.Type.ClrTypeName}>({ordinalExpr})";
            // NullabilityInference's own evidence -- a genuinely-null value would otherwise
            // throw INSIDE GetFieldValue<T> regardless of the C# property's own nullable
            // annotation (ADO.NET only ever checks IsDBNull, never the CLR type), so this
            // guard matters even for a nullable STRING column, unlike EntityEmitter/SqlRowEmitter's
            // own "string never needs a type change" exclusion.
            if (nullableColumnNames?.Contains(column.PipelineColumnName) ?? false)
                readExpr = $"reader.IsDBNull({ordinalExpr}) ? null : {readExpr}";

            assignments.Add($"        {identifierOf(column.PipelineColumnName)} = {readExpr},");
        }

        if (assignments.Count == 0)
        {
            return new EmitResult([], [new GenerationGap($"{rowClassName}Reader", "no columns to read -- see this row type's own SqlRowEmitter gaps")]);
        }

        var lines = new List<string>
        {
            "using System.Data.Common;",
            "",
            $"namespace {ns};",
            "",
            $"public static class {rowClassName}Reader",
            "{",
            $"    public static {rowClassName} Read(DbDataReader reader) => new()",
            "    {",
        };
        lines.AddRange(assignments);
        lines.Add("    };");
        lines.Add("}");

        return new EmitResult([new GeneratedFile($"Sql/{rowClassName}Reader.cs", Rendering.JoinLines(lines))], []);
    }
}
