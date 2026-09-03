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
    /// <summary>Sum(4)/Average(5)/Minimum(6)/Maximum(7) can genuinely produce NULL -- when a
    /// group has zero non-null source values -- confirmed via a real dtexec run against a
    /// deliberately all-NULL group (gap-audit Phase 3.3, 2026-09-02, see AggregatePayload's own
    /// doc comment). GroupBy/Count/CountAll/CountDistinct never can. Excluded from string columns
    /// (never evidenced -- every measured function targets a numeric source column). Internal,
    /// not private -- <c>PackageGenerator</c> reuses the identical set to decide the DESTINATION
    /// entity's own property nullability, so the two can never drift apart (same sharing
    /// precedent as <c>TransformEmitter.CollectSsisFunctions</c>).</summary>
    internal static readonly HashSet<int> NullableAggregationTypes = [4, 5, 6, 7];

    public static EmitResult Emit(string ns, string rowClassName, AggregatePlan aggregate)
    {
        var component = aggregate.Component;
        var output = component.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        if (output is null)
        {
            return new EmitResult([], [new GenerationGap(rowClassName, $"Aggregate '{component.Name}' has no main (non-error) output")]);
        }

        var nullableColumnNames = new HashSet<string>(
            aggregate.Functions.Where(f => NullableAggregationTypes.Contains(f.AggregationTypeRaw)).Select(f => f.OutputColumnName));

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
            var isNullable = type.ClrTypeName != "string" && nullableColumnNames.Contains(column.Name);
            var clrTypeName = isNullable ? type.ClrTypeName + "?" : type.ClrTypeName;
            var initializer = !isNullable && type.ClrTypeName == "string" ? " = \"\";" : "";
            propertyLines.Add($"    public {clrTypeName} {column.Name} {{ get; set; }}{initializer}");
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
