using System.Globalization;
using System.Text;

namespace Svk.Core;

/// <summary>
/// Writes a package's synthetic sample data to disk: a CSV per Flat File/Excel source, a
/// CREATE TABLE + INSERT script per SQL-shaped source, a runnable schema.sql covering every
/// destination table, and (for any Lookup with a resolvable join key) a reference-table seed
/// sharing keys with the main flow so both the match and no-match paths get exercised.
/// </summary>
public static class SampleDataWriter
{
    public static List<string> Write(PackagePlan plan, string outDir, int rows, int seed)
    {
        Directory.CreateDirectory(outDir);
        var written = new List<string>();
        var keyPoolsByInputColumn = new Dictionary<string, SharedKeyPool>(StringComparer.OrdinalIgnoreCase);

        foreach (var lookup in plan.Lookups)
        {
            written.Add(WriteLookupReference(plan, lookup, outDir, rows, seed, keyPoolsByInputColumn));
        }

        foreach (var src in plan.Sources)
        {
            written.Add(WriteSource(plan, src, outDir, rows, seed, keyPoolsByInputColumn));
        }

        var schemaLines = WriteDestinations(plan, outDir, written);
        if (schemaLines.Count > 0)
        {
            File.WriteAllText(Path.Combine(outDir, "schema.sql"), string.Join("\n\n", schemaLines) + "\n");
            written.Add("schema.sql (every destination table in this package)");
        }

        return written;
    }

    private static string WriteLookupReference(PackagePlan plan, LookupTouchPoint lookup, string outDir, int rows, int seed, Dictionary<string, SharedKeyPool> keyPoolsByInputColumn)
    {
        if (lookup.JoinKey is not { } jk)
        {
            return $"lookup '{lookup.ComponentName}': no join key declared in the .dtsx -- skipped (needs a confirmed Tier-1 decision, see ssisx generate's own decisions.json mechanism)";
        }

        var refType = lookup.ReferenceColumns.FirstOrDefault(c => string.Equals(c.Name, jk.ReferenceColumn, StringComparison.OrdinalIgnoreCase));
        var keyRng = ValueSynthesizer.RngFor(plan.PackageName, lookup.ComponentName, jk.ReferenceColumn, seed, rowIndex: 0);
        var pool = new SharedKeyPool(
            matchableCount: Math.Max(3, rows / 3),
            mainFlowOnlyCount: Math.Max(2, rows / 5),
            keyFactory: r => ValueSynthesizer.Synthesize(refType?.PipelineDataType ?? "i4", refType?.Length, refType?.Precision, refType?.Scale, r, allowNull: false)
                             ?? r.Next(1, 100_000),
            rng: keyRng);
        keyPoolsByInputColumn[jk.InputColumn] = pool;

        var refColumns = lookup.ReferenceColumns.Count > 0
            ? lookup.ReferenceColumns
            : [new ColumnSchema(jk.ReferenceColumn, "i4", null, null, null)];

        var fromTable = Naming.TryExtractSingleFromTable(lookup.SqlCommand);
        var refTableName = Naming.NormalizeTableName(fromTable ?? lookup.ComponentName);

        var refRows = new List<Dictionary<string, object?>>();
        for (var i = 0; i < pool.ReferenceTableKeys.Count; i++)
        {
            var row = GenerateRow(plan.PackageName, lookup.ComponentName, refColumns, seed, i, keyPoolsByInputColumn: null);
            row[jk.ReferenceColumn] = pool.ReferenceTableKeys[i];
            refRows.Add(row);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"-- Reference table for Lookup '{lookup.ComponentName}'.");
        sb.AppendLine(fromTable is null
            ? "-- Table name below is a best-effort fallback (no single FROM/JOIN table found in the reference query) -- rename before use."
            : "-- Table name resolved from the Lookup's own reference query.");
        sb.AppendLine($"-- Seeded so ~70% of the main flow's own '{jk.InputColumn}' values match a row here (match path) and the rest deliberately don't (no-match path).");
        sb.AppendLine();
        sb.Append(RenderSqlSeed(refTableName, refColumns, refRows));

        var refPath = Path.Combine(outDir, $"{Naming.SafeFileName(lookup.ComponentName)}.reference.sql");
        File.WriteAllText(refPath, sb.ToString());
        return $"lookup reference '{lookup.ComponentName}' -> {Path.GetFileName(refPath)} (table {refTableName}, {refRows.Count} rows)";
    }

    private static string WriteSource(PackagePlan plan, SourceTouchPoint src, string outDir, int rows, int seed, Dictionary<string, SharedKeyPool> keyPoolsByInputColumn)
    {
        var rowsData = new List<Dictionary<string, object?>>();
        for (var i = 0; i < rows; i++)
        {
            rowsData.Add(GenerateRow(plan.PackageName, src.ComponentName, src.Columns, seed, i, keyPoolsByInputColumn));
        }

        if (src.Kind is TouchPointKind.FlatFileSource or TouchPointKind.ExcelSource)
        {
            var csvPath = Path.Combine(outDir, $"{Naming.SafeFileName(src.ComponentName)}.csv");
            File.WriteAllText(csvPath, RenderCsv(src.Columns, rowsData));
            return $"{src.Kind} '{src.ComponentName}' -> {Path.GetFileName(csvPath)} ({rows} rows)";
        }

        var tableName = Naming.NormalizeTableName(src.TargetTableOrFile ?? src.ComponentName);
        var sqlPath = Path.Combine(outDir, $"{Naming.SafeFileName(src.ComponentName)}.sql");
        File.WriteAllText(sqlPath, RenderSqlSeed(tableName, src.Columns, rowsData));
        return $"{src.Kind} '{src.ComponentName}' -> {Path.GetFileName(sqlPath)} (table {tableName}, {rows} rows)";
    }

    private static List<string> WriteDestinations(PackagePlan plan, string outDir, List<string> written)
    {
        var schemaLines = new List<string>();
        if (plan.Destinations.Count == 0) return schemaLines;

        schemaLines.Add(
            $"-- Destination schema for package '{plan.PackageName}' -- built from spec.json's own ExternalMetadataColumns.\n" +
            "-- No .dtsx carries primary-key/nullability/identity information; PK lines are INFERRED, not authoritative -- verify before relying on them.");

        foreach (var dest in plan.Destinations)
        {
            var tableName = Naming.NormalizeTableName(dest.TableName ?? dest.ComponentName);
            var ddl = SchemaEmitter.RenderCreateTable(tableName, dest.Columns, dest.PrimaryKey);
            schemaLines.Add(ddl);

            var expectedPath = Path.Combine(outDir, $"{Naming.SafeFileName(dest.ComponentName)}.expected.sql");
            File.WriteAllText(expectedPath, ddl + "\n");
            written.Add($"destination '{dest.ComponentName}' -> {Path.GetFileName(expectedPath)} (table {tableName})");
        }

        return schemaLines;
    }

    private static Dictionary<string, object?> GenerateRow(string packageName, string componentName, IReadOnlyList<ColumnSchema> columns, int seed, int rowIndex, Dictionary<string, SharedKeyPool>? keyPoolsByInputColumn)
    {
        var row = new Dictionary<string, object?>();
        foreach (var col in columns)
        {
            if (keyPoolsByInputColumn is not null && keyPoolsByInputColumn.TryGetValue(col.Name, out var pool))
            {
                row[col.Name] = pool.MainFlowKeyForRow(rowIndex);
                continue;
            }

            if (col.PipelineDataType is null)
            {
                row[col.Name] = null;
                continue;
            }

            var rng = ValueSynthesizer.RngFor(packageName, componentName, col.Name, seed, rowIndex);
            row[col.Name] = ValueSynthesizer.Synthesize(col.PipelineDataType, col.Length, col.Precision, col.Scale, rng, allowNull: true);
        }
        return row;
    }

    private static string RenderCsv(IReadOnlyList<ColumnSchema> columns, List<Dictionary<string, object?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns.Select(c => CsvField(c.Name))));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", columns.Select(c => CsvField(FormatValue(row[c.Name])))));
        }
        return sb.ToString();
    }

    private static string CsvField(string? value)
    {
        value ??= "";
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static string RenderSqlSeed(string tableName, IReadOnlyList<ColumnSchema> columns, List<Dictionary<string, object?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(SchemaEmitter.RenderCreateTable(tableName, columns, primaryKey: null));
        sb.AppendLine();
        var columnList = string.Join(", ", columns.Select(c => $"[{c.Name}]"));
        foreach (var row in rows)
        {
            var values = string.Join(", ", columns.Select(c => SqlLiteral(row[c.Name])));
            sb.AppendLine($"INSERT INTO {tableName} ({columnList}) VALUES ({values});");
        }
        return sb.ToString();
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "",
        DateOnly d => d.ToString("yyyy-MM-dd"),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss zzz"),
        TimeSpan ts => ts.ToString(@"hh\:mm\:ss"),
        bool b => b ? "1" : "0",
        decimal dec => dec.ToString(CultureInfo.InvariantCulture),
        float f => f.ToString(CultureInfo.InvariantCulture),
        double db => db.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static string SqlLiteral(object? value) => value switch
    {
        null => "NULL",
        string s => "N'" + s.Replace("'", "''") + "'",
        DateOnly d => $"'{d:yyyy-MM-dd}'",
        DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
        DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss zzz}'",
        TimeSpan ts => $"'{ts:hh\\:mm\\:ss}'",
        bool b => b ? "1" : "0",
        decimal dec => dec.ToString(CultureInfo.InvariantCulture),
        float f => f.ToString(CultureInfo.InvariantCulture),
        double db => db.ToString(CultureInfo.InvariantCulture),
        Guid g => $"'{g}'",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
    };
}
