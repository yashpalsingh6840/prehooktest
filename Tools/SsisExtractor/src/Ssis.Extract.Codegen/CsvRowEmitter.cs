using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits a Flat File Source's pipeline buffer shape, e.g. LoadEmployees.Csv.EmployeeCsvRow.
/// Source of truth is <see cref="ConnectionManagerSpec.FlatFileFormat"/>.Columns -- order is
/// authoritative there (a flat-file column carries no separate ordinal), so property
/// declaration order matches it exactly, which also matches the hand-written CSV row types.
///
/// A Flat File SOURCE COMPONENT can independently override a column's real pipeline output
/// type from what the connection manager itself declares -- confirmed real (2026-09-16, a
/// third-party portfolio batch run) from a package where the CM declares a column DT_STR
/// (raw text) but the Flat File Source's own output column for it is DT_I4
/// (ErrorOrTruncationOperation="Conversion"), with NO Data Conversion/Derived Column anywhere
/// downstream -- the source itself converts inline while reading, failing the component on bad
/// data per its own TruncationRowDisposition. Before this was recognized, the generated CSV row
/// property was typed purely from the CM's declared (pre-conversion) type, silently disagreeing
/// with the destination/transform (which correctly resolve the POST-conversion type from the
/// source's own output column) -- a real CS0029 build failure, not caught by TransformEmitter's
/// own mismatch gate since that gate only ever compares the destination side against ITSELF.
/// <paramref name="sourceComponent"/>, when supplied, lets this emitter prefer the source's own
/// output-column type whenever it disagrees with the CM's declared type for that column name.
/// </summary>
public static class CsvRowEmitter
{
    public static EmitResult Emit(string ns, string rowClassName, ConnectionManagerSpec connectionManager, PipelineComponentSpec? sourceComponent = null)
    {
        var format = connectionManager.FlatFileFormat;
        if (format is null)
        {
            return new EmitResult([], [new GenerationGap(rowClassName,
                $"connection manager '{connectionManager.ObjectName}' has no FlatFileFormat (CreationName='{connectionManager.CreationName}')")]);
        }

        var sourceOutputTypeByName = ResolveSourceOutputTypesByName(sourceComponent);

        // ClassMapEmitter independently reproduces this exact mapping (same format.Columns list,
        // same order) -- see PackageGenerator.MakeColumnIdentifierResolver's own doc comment.
        var identifierOf = PackageGenerator.MakeColumnIdentifierResolver();

        var gaps = new List<GenerationGap>();
        var propertyLines = new List<string>();
        var usesSsisWidth = false;

        foreach (var column in format.Columns)
        {
            var effectiveDataTypeName = column.DataTypeName;
            if (sourceOutputTypeByName.TryGetValue(column.ObjectName, out var sourceDataType)
                && sourceDataType is not null
                && !string.Equals(sourceDataType, column.DataTypeName, StringComparison.OrdinalIgnoreCase))
            {
                effectiveDataTypeName = sourceDataType;
            }

            var type = SsisPipelineTypeMap.Resolve(ToPipelineTypeKey(effectiveDataTypeName));
            if (type is null)
            {
                gaps.Add(new GenerationGap($"{rowClassName}.{column.ObjectName}",
                    effectiveDataTypeName == column.DataTypeName
                        ? $"unmapped flat-file data type '{column.DataTypeName}'"
                        : $"the Flat File Source's own output column overrides this column's type to '{effectiveDataTypeName}' (connection manager declares '{column.DataTypeName}'), and '{effectiveDataTypeName}' is unmapped"));
                continue;
            }

            if (propertyLines.Count > 0) propertyLines.Add("");

            if (type.ClrTypeName == "string" && column.MaximumWidth is int width)
            {
                propertyLines.Add($"    [SsisWidth({width})]");
                usesSsisWidth = true;
            }

            var initializer = type.ClrTypeName == "string" ? " = \"\";" : "";
            propertyLines.Add($"    public {type.ClrTypeName} {identifierOf(column.ObjectName)} {{ get; set; }}{initializer}");
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

    /// <summary>Maps a Flat File Source component's own non-error, non-dangling output columns
    /// by name to their declared <c>DataType</c> (the real, post-conversion pipeline buffer
    /// type) -- empty when <paramref name="sourceComponent"/> is null or has no such output, so
    /// every existing caller that doesn't (yet) pass one behaves exactly as before.</summary>
    /// <summary>Internal (not private) so <see cref="SampleDataEmitter"/>'s own CSV path can
    /// apply the exact same source-output-type-override resolution when synthesizing a
    /// representative value -- otherwise a column this fix retypes to e.g. <c>int</c> would still
    /// get a non-numeric "Sample" placeholder, since SampleDataEmitter previously resolved a
    /// column's type from the connection manager alone too (the identical bug, confirmed real by
    /// the starter test for this exact case actually failing to parse "Sample" as int once
    /// CsvRowEmitter alone was fixed).</summary>
    internal static Dictionary<string, string?> ResolveSourceOutputTypesByName(PipelineComponentSpec? sourceComponent)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (sourceComponent is null) return result;

        var output = sourceComponent.Outputs.FirstOrDefault(o => o.IsErrorOut != true && o.Dangling != true);
        if (output is null) return result;

        foreach (var column in output.Columns)
        {
            result[column.Name] = column.DataType;
        }

        return result;
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
