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
    /// <summary><paramref name="extraProperties"/> -- added closing a real gap found reviewing
    /// RBC_Demo_ETL's own real Package.dtsx: an error-redirect destination's own target table
    /// (dbo.StagingCustomers_Errors) has a FailedAt column with no corresponding
    /// &lt;inputColumn&gt; at all (a DB-side default, never mapped from the pipeline) -- invisible
    /// to <see cref="PipelineResolver.ResolveDestinationInput"/> by construction, since that walks
    /// only <c>destinationComponent</c>'s own mapped input columns. A property named here is
    /// appended verbatim, with NO pipeline/nullability resolution at all (the caller supplies the
    /// exact CLR type name) -- this is for a column the SINK itself populates at write time, not
    /// one derivable from any upstream row.</summary>
    public static EmitResult Emit(string ns, string entityName, PipelineComponentSpec destinationComponent,
        IReadOnlySet<string>? nullableColumnNames = null,
        IReadOnlyList<(string Name, string ClrType)>? extraProperties = null)
    {
        var resolved = PipelineResolver.ResolveDestinationInput(destinationComponent);
        var gaps = resolved.Unresolved
            .Select(u => new GenerationGap($"{entityName}.{u.ColumnName}", u.Reason))
            .ToList();

        // DbContextEmitter independently resolves this SAME PipelineResolver.ResolveDestinationInput
        // list, in the same order, so it reproduces this exact identifier mapping with no state
        // shared between them (see PackageGenerator.MakeColumnIdentifierResolver's own doc
        // comment) -- and, wherever a real column's raw external name differs from its sanitized
        // identifier, DbContextEmitter emits an explicit .HasColumnName(...) so EF still writes
        // to the column's true real name (e.g. "WWI Stock Item ID"), not its sanitized property
        // name.
        var identifierOf = PackageGenerator.MakeColumnIdentifierResolver();

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
            propertyLines.Add($"    public {typeName} {identifierOf(column.ExternalColumnName)} {{ get; set; }}{initializer}");
        }

        foreach (var (name, clrType) in extraProperties ?? [])
        {
            if (propertyLines.Count > 0) propertyLines.Add("");
            // Caller-supplied (e.g. "FailedAt"), never a raw external column name -- already a
            // safe identifier, but still routed through the resolver so it can't collide with a
            // real column's own sanitized name.
            propertyLines.Add($"    public {clrType} {identifierOf(name)} {{ get; set; }}");
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
