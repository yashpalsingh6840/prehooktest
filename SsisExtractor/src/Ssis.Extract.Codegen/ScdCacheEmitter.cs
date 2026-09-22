using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits a <c>Microsoft.SCD</c> ("Slowly Changing Dimension") reference cache -- the dimension's
/// CURRENT rows, keyed by business key, loaded once up front. Phase 7 of the
/// unsupported-component-types plan.
///
/// <para><b>Deliberately the same shape and the same mechanism as <see cref="LookupCacheEmitter"/></b>
/// rather than a second reference-read path: one query, run once, into a dictionary. The two differ
/// only in what the dictionary VALUE is -- a Lookup needs a named row type because its downstream
/// transform reads individual reference columns by name (<c>cache[key].Region</c>), while an SCD
/// only ever needs the compared attributes as a positional vector to diff against the incoming row,
/// so an <c>object?[]</c> in the generator's own already-decided attribute order is both sufficient
/// and simpler (no second generated type, and the array lines up index-for-index with the
/// <c>attributeValues</c> selector and the <c>ScdColumnRole</c> array the same call site emits).</para>
///
/// <para><b>Columns are read by NAME, not position</b> -- unlike <see cref="LookupCacheEmitter"/>,
/// and for a real evidenced reason: an SCD's dimension query is wizard-generated and its SELECT list
/// order does not follow the input columns at all (the one real evidenced package selects
/// <c>[Designation], [EmpId], [FirstName], [LastName], [StartDate], [EndDate]</c> while its input
/// columns arrive in a different order, and two of those six are not input columns at all -- they
/// exist only to satisfy <c>CurrentRowWhere</c>). Reading by position here would silently compare the
/// wrong columns. The generated code therefore ASSUMES the query exposes the business key and every
/// compared attribute under their SSIS column names -- the same stated assumption an OLE DB Source
/// in SqlCommand mode already carries, and a wrong one fails loudly at <c>GetOrdinal</c> rather than
/// producing wrong classifications.</para>
///
/// <para><b>Duplicate business keys keep the FIRST row</b> (<c>TryAdd</c>), matching the measured
/// behaviour of a full-cache Lookup against this same SQL Server -- see
/// <see cref="LookupCacheEmitter"/>'s own note on that measurement. A dimension with two rows
/// satisfying <c>CurrentRowWhere</c> for one business key is malformed regardless.</para>
///
/// <para><b>A composite business key</b> (more than one declared column, Phase 4 of the gap-audit
/// plan concurrent-whistling-turing.md, 2026-09-16) becomes a plain, unnamed C# tuple -- measured
/// via a real dtexec probe (2 key columns, seeded rows sharing one column but differing on the
/// other) to confirm SSIS matches on AND-of-equality across every declared key column, exactly
/// what a <c>ValueTuple</c>'s own structural equality already gives for free. Byte-identical to
/// the original single-column shape whenever there is exactly one key column.</para>
/// </summary>
public static class ScdCacheEmitter
{
    public static EmitResult Emit(
        string ns,
        string cacheClassName,
        string referenceSql,
        IReadOnlyList<string> businessKeyColumns,
        IReadOnlyList<SsisPipelineType> businessKeyTypes,
        IReadOnlyList<string> attributeColumns)
    {
        if (attributeColumns.Count == 0)
        {
            return new EmitResult([], [new GenerationGap(cacheClassName, "no compared attribute columns -- nothing to cache")]);
        }

        var isComposite = businessKeyColumns.Count > 1;
        var keyClrType = isComposite
            ? $"({string.Join(", ", businessKeyTypes.Select(t => t.ClrTypeName))})"
            : businessKeyTypes[0].ClrTypeName;

        var lines = new List<string>
        {
            "using Microsoft.Data.SqlClient;",
            "",
            $"namespace {ns};",
            "",
            $"/// <summary>The CURRENT rows of the dimension behind the '{cacheClassName}' Slowly Changing",
            "/// Dimension, keyed by business key. The value array holds the compared attributes in the",
            "/// exact order the generated step's own attribute-role array and value selector use, so the",
            "/// three line up index for index. Columns are read by NAME -- a Slowly Changing Dimension's",
            "/// own dimension query is wizard-generated and its SELECT list order does not match the input",
            "/// columns.</summary>",
            $"public static class {cacheClassName}",
            "{",
            $"    public static async Task<Dictionary<{keyClrType}, object?[]>> LoadAsync(string connectionString, CancellationToken ct)",
            "    {",
            $"        var result = new Dictionary<{keyClrType}, object?[]>();",
            "        await using var connection = new SqlConnection(connectionString);",
            "        await connection.OpenAsync(ct);",
            "        await using var command = connection.CreateCommand();",
            $"        command.CommandText = {ProgramEmitter.CSharpStringLiteral(referenceSql)};",
            "        await using var reader = await command.ExecuteReaderAsync(ct);",
            "",
        };

        if (isComposite)
        {
            for (var i = 0; i < businessKeyColumns.Count; i++)
                lines.Add($"        var keyOrdinal{i} = reader.GetOrdinal({ProgramEmitter.CSharpStringLiteral(businessKeyColumns[i])});");
        }
        else
        {
            lines.Add($"        var keyOrdinal = reader.GetOrdinal({ProgramEmitter.CSharpStringLiteral(businessKeyColumns[0])});");
        }

        for (var i = 0; i < attributeColumns.Count; i++)
            lines.Add($"        var ordinal{i} = reader.GetOrdinal({ProgramEmitter.CSharpStringLiteral(attributeColumns[i])});");

        lines.Add("");
        lines.Add("        while (await reader.ReadAsync(ct))");
        lines.Add("        {");
        lines.Add(isComposite
            ? $"            var key = ({string.Join(", ", businessKeyTypes.Select((t, i) => $"reader.GetFieldValue<{t.ClrTypeName}>(keyOrdinal{i})"))});"
            : $"            var key = reader.GetFieldValue<{businessKeyTypes[0].ClrTypeName}>(keyOrdinal);");
        // A NULL dimension attribute is a real possibility, and the classifier's own equality rule
        // already handles null on either side -- so it must reach it as null, not as DBNull.Value
        // (which would compare unequal to a genuinely null incoming value and misclassify the row).
        lines.Add("            result.TryAdd(key, new object?[]");
        lines.Add("            {");
        for (var i = 0; i < attributeColumns.Count; i++)
        {
            var comma = i < attributeColumns.Count - 1 ? "," : "";
            lines.Add($"                reader.IsDBNull(ordinal{i}) ? null : reader.GetValue(ordinal{i}){comma}");
        }
        lines.Add("            });");
        lines.Add("        }");
        lines.Add("");
        lines.Add("        return result;");
        lines.Add("    }");
        lines.Add("}");

        return new EmitResult([new GeneratedFile($"Mapping/{cacheClassName}.cs", Rendering.JoinLines(lines))], []);
    }
}
