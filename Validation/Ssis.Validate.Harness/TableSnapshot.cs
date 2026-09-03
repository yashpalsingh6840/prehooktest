using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace Ssis.Validate.Harness;

/// <summary>
/// A full-table capture: column names + one ordered value list per row. Deliberately
/// column-name-keyed per row (not a JSON object per row) so a snapshot file's byte order
/// is stable across captures -- a plain "row as object" shape would let .NET's own column
/// ordering wobble between runs, which would make every diff noisy on unchanged data.
/// </summary>
public sealed class TableSnapshot
{
    public required string Table { get; init; }
    public required List<string> Columns { get; init; }
    public required List<List<JsonElement?>> Rows { get; init; }
}

/// <summary>
/// Captures/restores/diffs table snapshots against a live SQL Server -- the container's, in
/// gate 3's real use, or this dev box's `.\SQLFORPOC` when capturing the golden corpus from
/// the actual PoC packages. No SSIS/GAC dependency (plan §5: gate 3 must run with neither).
/// </summary>
public static class SnapshotService
{
    public static async Task<TableSnapshot> Capture(string connectionString, string table, string orderByColumn)
    {
        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        using var cmd = new SqlCommand($"SELECT * FROM {table} ORDER BY {orderByColumn}", conn);
        using var reader = await cmd.ExecuteReaderAsync();

        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var rows = new List<List<JsonElement?>>();
        while (await reader.ReadAsync())
        {
            var row = new List<JsonElement?>(columns.Count);
            for (var i = 0; i < columns.Count; i++)
            {
                row.Add(ToJsonElement(reader.IsDBNull(i) ? null : reader.GetValue(i)));
            }
            rows.Add(row);
        }

        return new TableSnapshot { Table = table, Columns = columns, Rows = rows };
    }

    /// <summary>TRUNCATEs <paramref name="snapshot"/>'s table and re-inserts every row -- used to seed the container's "before" state from the golden corpus (plan §5 step 2).</summary>
    public static async Task Restore(string connectionString, TableSnapshot snapshot)
    {
        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        using (var truncate = new SqlCommand($"TRUNCATE TABLE {snapshot.Table}", conn))
        {
            await truncate.ExecuteNonQueryAsync();
        }

        if (snapshot.Rows.Count == 0) return;

        var columnList = string.Join(", ", snapshot.Columns);
        var paramList = string.Join(", ", snapshot.Columns.Select((_, i) => $"@p{i}"));
        var insertSql = $"INSERT INTO {snapshot.Table} ({columnList}) VALUES ({paramList})";

        foreach (var row in snapshot.Rows)
        {
            using var insert = new SqlCommand(insertSql, conn);
            for (var i = 0; i < row.Count; i++)
            {
                insert.Parameters.AddWithValue($"@p{i}", FromJsonElement(row[i]) ?? DBNull.Value);
            }
            await insert.ExecuteNonQueryAsync();
        }
    }

    public static void WriteToFile(TableSnapshot snapshot, string path)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, options));
    }

    public static TableSnapshot ReadFromFile(string path) =>
        JsonSerializer.Deserialize<TableSnapshot>(File.ReadAllText(path))
        ?? throw new InvalidOperationException($"could not deserialize snapshot from {path}");

    private static JsonElement? ToJsonElement(object? value)
    {
        if (value is null) return null;
        var json = value switch
        {
            string s => JsonSerializer.Serialize(s),
            bool b => b ? "true" : "false",
            DateTime dt => JsonSerializer.Serialize(dt.ToString("o")),
            decimal m => m.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => JsonSerializer.Serialize(value.ToString()),
        };
        // Numeric types render as bare JSON numbers (no quotes) so a diff can compare them
        // numerically rather than as strings; everything else (including dates, kept as ISO
        // 8601 text rather than a JSON date type SQL Server has no equivalent of) is a string.
        var isNumeric = value is byte or short or int or long or float or double or decimal;
        var text = isNumeric ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)! : json;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static object? FromJsonElement(JsonElement? element)
    {
        if (element is not { } e || e.ValueKind == JsonValueKind.Null) return null;
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidOperationException($"unsupported snapshot value kind {e.ValueKind}"),
        };
    }
}
