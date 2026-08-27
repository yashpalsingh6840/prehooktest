using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits a Flat File Source's pipeline buffer shape, e.g. LoadEmployees.Csv.EmployeeCsvRow.
/// Source of truth is <see cref="ConnectionManagerSpec.FlatFileFormat"/>.Columns -- order is
/// authoritative there (a flat-file column carries no separate ordinal), so property
/// declaration order matches it exactly, which also matches the hand-written CSV row types.
/// </summary>
public static class CsvRowEmitter
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
        var propertyLines = new List<string>();
        var usesSsisWidth = false;

        foreach (var column in format.Columns)
        {
            var type = SsisPipelineTypeMap.Resolve(ToPipelineTypeKey(column.DataTypeName));
            if (type is null)
            {
                gaps.Add(new GenerationGap($"{rowClassName}.{column.ObjectName}", $"unmapped flat-file data type '{column.DataTypeName}'"));
                continue;
            }

            if (propertyLines.Count > 0) propertyLines.Add("");

            if (type.ClrTypeName == "string" && column.MaximumWidth is int width)
            {
                propertyLines.Add($"    [SsisWidth({width})]");
                usesSsisWidth = true;
            }

            var initializer = type.ClrTypeName == "string" ? " = \"\";" : "";
            propertyLines.Add($"    public {type.ClrTypeName} {column.ObjectName} {{ get; set; }}{initializer}");
        }

        if (propertyLines.Count == 0)
        {
            if (gaps.Count == 0) gaps.Add(new GenerationGap(rowClassName, "no columns could be generated"));
            return new EmitResult([], gaps);
        }

        var lines = new List<string>();
        if (usesSsisWidth)
        {
            lines.Add("using Etl.Core.Ssis;");
            lines.Add("");
        }
        lines.Add($"namespace {ns};");
        lines.Add("");
        lines.Add($"public sealed class {rowClassName}");
        lines.Add("{");
        lines.AddRange(propertyLines);
        lines.Add("}");

        return new EmitResult([new GeneratedFile($"Csv/{rowClassName}.cs", Rendering.JoinLines(lines))], gaps);
    }

    /// <summary>"DT_WSTR" -> "wstr" -- bridges FlatFileColumnSpec's canonical DT_* form to the
    /// lowercase short form SsisPipelineTypeMap's keys use. Internal (not private) so
    /// ClassMapEmitter can apply the exact same gate when deciding which columns to Map --
    /// see that type's own comment for why the two must agree.</summary>
    internal static string ToPipelineTypeKey(string dataTypeName) =>
        dataTypeName.StartsWith("DT_", StringComparison.OrdinalIgnoreCase)
            ? dataTypeName[3..].ToLowerInvariant()
            : dataTypeName.ToLowerInvariant();
}
