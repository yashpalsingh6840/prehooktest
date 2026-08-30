using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits a Microsoft.Aggregate component's own OUTPUT row shape (e.g. LoadX.Sql.RegionRow) --
/// built speculatively 2026-08-30, see <c>AggregatePayload</c>'s own doc comment. Deliberately
/// NOT built on <see cref="Ssis.Extract.Dtsx.PipelineResolver"/> the way <see cref="SqlRowEmitter"/>
/// is: an Aggregate's own output has NO external metadata columns at all (confirmed real from
/// RBC_Demo_ETL's own AGG_ByRegion -- <c>&lt;externalMetadataColumns /&gt;</c>, empty -- since it
/// is a pure pipeline transform with no design-time database/file schema of its own, the exact
/// same reason <c>MergeJoinEmitter</c> resolves its own combined row type straight off the raw
/// output column's own dataType/length/precision/scale rather than through the external-metadata
/// join). Property names follow the Aggregate's own output column name -- what
/// <see cref="ProgramEmitter"/>'s generated <c>(key, rows) => new {RowType} { ... }</c> projection
/// references.
/// </summary>
public static class AggregateRowEmitter
{
    public static EmitResult Emit(string ns, string rowClassName, PipelineComponentSpec aggregate)
    {
        var output = aggregate.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        if (output is null)
        {
            return new EmitResult([], [new GenerationGap(rowClassName, $"Aggregate '{aggregate.Name}' has no main (non-error) output")]);
        }

        var gaps = new List<GenerationGap>();
        var propertyLines = new List<string>();
        foreach (var column in output.Columns)
        {
            var type = column.DataType is null ? null : SsisPipelineTypeMap.Resolve(column.DataType);
            if (type is null)
            {
                gaps.Add(new GenerationGap($"{rowClassName}.{column.Name}",
                    $"unmapped pipeline data type '{column.DataType}' -- add it to SsisPipelineTypeMap before this column can be generated"));
                continue;
            }

            if (propertyLines.Count > 0) propertyLines.Add("");
            var initializer = type.ClrTypeName == "string" ? " = \"\";" : "";
            propertyLines.Add($"    public {type.ClrTypeName} {column.Name} {{ get; set; }}{initializer}");
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
