using System.Text;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Shared;

namespace Svk.Core;

/// <summary>
/// Renders a runnable CREATE TABLE statement from a destination's own declared column
/// contract. A `.dtsx` carries no primary-key/nullability/identity concept at all (see
/// PrimaryKeyCandidateSpec's own doc comment) -- every column is emitted NULL, and a PK line
/// is added only when PrimaryKeyInference produced a candidate, explicitly marked inferred.
/// </summary>
public static class SchemaEmitter
{
    private static readonly Dictionary<string, string> DirectSqlType = new(StringComparer.Ordinal)
    {
        ["sbyte"] = "smallint",
        ["short"] = "smallint",
        ["int"] = "int",
        ["long"] = "bigint",
        ["byte"] = "tinyint",
        ["float"] = "real",
        ["double"] = "float",
        ["bool"] = "bit",
        ["Guid"] = "uniqueidentifier",
    };

    public static string RenderCreateTable(string tableName, IReadOnlyList<ColumnSchema> columns, PrimaryKeyCandidateSpec? primaryKey)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE {tableName} (");

        var lines = columns.Select(c => $"    [{c.Name}] {RenderSqlType(c)} NULL").ToList();
        if (primaryKey is { Columns.Count: > 0 })
        {
            var pkCols = string.Join(", ", primaryKey.Columns.Select(c => $"[{c}]"));
            lines.Add($"    CONSTRAINT [PK_{Naming.SanitizeIdentifier(tableName)}] PRIMARY KEY ({pkCols}) -- INFERRED ({primaryKey.Confidence}): {primaryKey.Reason}");
        }

        sb.AppendLine(string.Join(",\n", lines));
        sb.Append(");");
        return sb.ToString();
    }

    private static string RenderSqlType(ColumnSchema column)
    {
        var type = SsisPipelineTypeMap.Resolve(column.PipelineDataType);
        if (type is null)
        {
            return $"nvarchar(4000) /* unmapped SSIS type: {column.PipelineDataType ?? "?"} */";
        }

        if (type.Facet == SsisFacetKind.ColumnType)
        {
            return SsisPipelineTypeMap.RenderColumnType(type, column.Length, column.Precision, column.Scale)
                   ?? $"nvarchar(4000) /* missing length/precision/scale for {type.DtName} */";
        }

        if (type.Facet == SsisFacetKind.MaxLength)
        {
            var len = column.Length is > 0 ? column.Length.ToString() : "max";
            return type.DtName == "DT_STR" ? $"varchar({len})" : $"nvarchar({len})";
        }

        return DirectSqlType.TryGetValue(type.ClrTypeName, out var sql)
            ? sql
            : $"nvarchar(4000) /* unmapped CLR type: {type.ClrTypeName} */";
    }
}
