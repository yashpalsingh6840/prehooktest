using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits one destination table's plain DTO entity class, e.g. LoadEmployees.Model.Employee.
/// Source of truth is <see cref="PipelineResolver.ResolveDestinationInput"/>, which already
/// does the buffer-column <-> external-metadata-column join and carries the resolved CLR type
/// per column -- this emitter just renders it.
///
/// Property declaration order follows the OLE DB Destination's own input column order (XML
/// document order), not any cosmetic ordering a human might choose by hand -- deterministic
/// and traceable back to the source, at the cost of not byte-matching the hand-written
/// LoadEmployees/Model/Employee.cs, which orders EmployeeKey right after EmployeeID.
/// </summary>
public static class EntityEmitter
{
    public static EmitResult Emit(string ns, string entityName, PipelineComponentSpec destinationComponent, IReadOnlySet<string>? nullableColumnNames = null)
    {
        var resolved = PipelineResolver.ResolveDestinationInput(destinationComponent);
        var gaps = resolved.Unresolved
            .Select(u => new GenerationGap($"{entityName}.{u.ColumnName}", u.Reason))
            .ToList();

        var propertyLines = new List<string>();
        foreach (var column in resolved.Columns)
        {
            if (column.Type is null)
            {
                gaps.Add(new GenerationGap($"{entityName}.{column.ExternalColumnName}",
                    $"unmapped pipeline data type '{column.ExternalDataType}' -- add it to SsisPipelineTypeMap before this column can be generated"));
                continue;
            }

            if (propertyLines.Count > 0) propertyLines.Add("");
            // A passthrough column's own source buffer name equals column.PipelineColumnName --
            // if THAT column is nullable-inferred (NullabilityInference), the source row's own
            // property is a nullable CLR type, so this entity's own property must be too or a
            // plain assignment (SqlRowEmitter's row -> this) won't compile. string is NOT
            // excluded (see SqlRowEmitter's identical check, fixed 2026-08-28 for the same
            // reason): this project generates with <Nullable>enable</Nullable> +
            // TreatWarningsAsErrors, so a `string` property assigned a genuinely-null value is a
            // build ERROR (CS8601), not just a runtime risk.
            var isNullable = nullableColumnNames?.Contains(column.PipelineColumnName) ?? false;
            var typeName = isNullable ? column.Type.ClrTypeName + "?" : column.Type.ClrTypeName;
            var initializer = !isNullable && column.Type.ClrTypeName == "string" ? " = \"\";" : "";
            propertyLines.Add($"    public {typeName} {column.ExternalColumnName} {{ get; set; }}{initializer}");
        }

        if (propertyLines.Count == 0)
        {
            if (gaps.Count == 0)
                gaps.Add(new GenerationGap(entityName, "destination has no resolvable columns"));
            return new EmitResult([], gaps);
        }

        var lines = new List<string> { $"namespace {ns};", "", $"public sealed class {entityName}", "{" };
        lines.AddRange(propertyLines);
        lines.Add("}");

        return new EmitResult([new GeneratedFile($"Model/{entityName}.cs", Rendering.JoinLines(lines))], gaps);
    }
}
