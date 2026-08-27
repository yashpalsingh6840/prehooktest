using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits a full-cache reference-table preload scaffold for a Lookup component, e.g.
/// LoadX.Mapping.LookupCustomerCache. Deliberately NOT wired into Program.cs by
/// <see cref="PackageGenerator"/> -- SSIS does not persist a Lookup's join key anywhere in the
/// saved .dtsx (confirmed via a real object-model round-trip; see
/// docs/report-schema.md's "Still not attempted" note under the synthetic component-coverage
/// fixtures), so there is no safe way to generate a join condition without guessing one. A
/// guessed join key would compile, run, and silently produce wrong joined data on every row --
/// exactly the failure category "gaps not guesses" exists to prevent, and strictly worse than
/// not generating the flow at all.
///
/// What IS 100% derivable from the spec, and so IS generated: the reference row shape (from
/// <see cref="LookupPayload.ReferenceColumns"/>) and a generic
/// <c>LoadAsync&lt;TKey&gt;(connectionString, keySelector, ct)</c> that runs the Lookup's own
/// <see cref="LookupPayload.SqlCommand"/> verbatim and builds a <c>Dictionary&lt;TKey,
/// ReferenceRow&gt;</c> -- deferring "what's the key" entirely to hand-written caller code, so
/// this scaffold needs zero guessed information to compile standalone.
///
/// Reference columns are read by POSITION, not by name -- <see cref="LookupPayload"/> carries
/// no independent ordinal for them beyond <see cref="LookupPayload.SqlCommand"/>'s own SELECT
/// list order, the only ordering this tool has evidence for.
/// </summary>
public static class LookupCacheEmitter
{
    public static EmitResult Emit(string ns, string cacheClassName, PipelineComponentSpec lookupComponent)
    {
        var payload = lookupComponent.Lookup;
        if (payload is null)
        {
            return new EmitResult([], [new GenerationGap(cacheClassName, $"'{lookupComponent.Name}' has no Lookup payload")]);
        }

        if (string.IsNullOrEmpty(payload.SqlCommand))
        {
            return new EmitResult([], [new GenerationGap(cacheClassName, $"Lookup '{lookupComponent.Name}' has no SqlCommand -- cannot build a reference-table cache")]);
        }

        if (payload.CacheTypeRaw is not null and not 0)
        {
            return new EmitResult([], [new GenerationGap(cacheClassName,
                $"Lookup '{lookupComponent.Name}' has CacheType={payload.CacheTypeRaw} -- only full cache (0) is supported yet; partial/no-cache Lookup needs per-row query semantics this tool doesn't generate")]);
        }

        var gaps = new List<GenerationGap>();
        var propertyLines = new List<string>();
        var readerLines = new List<string>();
        var ordinal = 0;
        foreach (var col in payload.ReferenceColumns)
        {
            var type = SsisPipelineTypeMap.Resolve(CsvRowEmitter.ToPipelineTypeKey(col.DataType ?? ""));
            if (type is null)
            {
                gaps.Add(new GenerationGap($"{cacheClassName}.{col.Name}", $"unmapped reference column data type '{col.DataType}'"));
                continue;
            }

            if (propertyLines.Count > 0) propertyLines.Add("");
            var initializer = type.ClrTypeName == "string" ? " = \"\";" : "";
            propertyLines.Add($"        public {type.ClrTypeName} {col.Name} {{ get; set; }}{initializer}");
            readerLines.Add($"                {col.Name} = reader.GetFieldValue<{type.ClrTypeName}>({ordinal}),");
            ordinal++;
        }

        if (propertyLines.Count == 0)
        {
            if (gaps.Count == 0) gaps.Add(new GenerationGap(cacheClassName, "no reference columns could be generated"));
            return new EmitResult([], gaps);
        }

        var lines = new List<string>
        {
            "using System.Data.Common;",
            "using Microsoft.Data.SqlClient;",
            "",
            $"namespace {ns};",
            "",
            $"/// <summary>Full-cache preload scaffold for the '{lookupComponent.Name}' Lookup. NOT",
            "/// referenced by Program.cs -- SSIS does not persist a Lookup's join key anywhere in the",
            "/// saved .dtsx. Pick the join key yourself, then wire this into a real",
            "/// IRowTransform&lt;TRow,TEntity&gt;.</summary>",
            $"public static class {cacheClassName}",
            "{",
            "    public sealed class ReferenceRow",
            "    {",
        };
        lines.AddRange(propertyLines);
        lines.Add("    }");
        lines.Add("");
        lines.Add("    public static async Task<Dictionary<TKey, ReferenceRow>> LoadAsync<TKey>(");
        lines.Add("        string connectionString, Func<ReferenceRow, TKey> keySelector, CancellationToken ct)");
        lines.Add("        where TKey : notnull");
        lines.Add("    {");
        lines.Add("        var result = new Dictionary<TKey, ReferenceRow>();");
        lines.Add("        await using var connection = new SqlConnection(connectionString);");
        lines.Add("        await connection.OpenAsync(ct);");
        lines.Add("        await using var command = connection.CreateCommand();");
        lines.Add($"        command.CommandText = {CSharpStringLiteral(payload.SqlCommand)};");
        lines.Add("        await using var reader = await command.ExecuteReaderAsync(ct);");
        lines.Add("        while (await reader.ReadAsync(ct))");
        lines.Add("        {");
        lines.Add("            var row = new ReferenceRow");
        lines.Add("            {");
        lines.AddRange(readerLines);
        lines.Add("            };");
        lines.Add("            result[keySelector(row)] = row;");
        lines.Add("        }");
        lines.Add("        return result;");
        lines.Add("    }");
        lines.Add("}");

        return new EmitResult([new GeneratedFile($"Mapping/{cacheClassName}.cs", Rendering.JoinLines(lines))], gaps);
    }

    private static string CSharpStringLiteral(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
