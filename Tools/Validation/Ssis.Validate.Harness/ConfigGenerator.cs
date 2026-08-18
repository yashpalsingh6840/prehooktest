using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ssis.Validate.Harness;

/// <summary>One row of `ssisx report`'s nondeterministic.json -- mirrors <c>Ssis.Extract.Model.Analysis.NonDeterministicColumnSpec</c> without a project reference (this tool is deliberately not coupled to the extractor's own assembly).</summary>
internal sealed class NonDeterministicRow
{
    public string? PackageName { get; set; }
    public string? TargetTable { get; set; }
    public string? TargetColumn { get; set; }
}

/// <summary>
/// Generates a starter <see cref="PackageConfig"/> from `ssisx report`'s non-determinism
/// manifest -- the ExcludedColumns list is real, generated data; KeyColumn is deliberately
/// left for a human to fill in (plan §12: "reviewed by a human"), since the extractor does
/// not currently detect primary keys from the .dtsx alone.
/// </summary>
public static class ConfigGenerator
{
    public static PackageConfig Generate(string packageName, string schemaScript, List<string> targetTables, string nonDeterministicJsonPath)
    {
        var rows = File.Exists(nonDeterministicJsonPath)
            ? JsonSerializer.Deserialize<List<NonDeterministicRow>>(File.ReadAllText(nonDeterministicJsonPath)) ?? []
            : [];

        var config = new PackageConfig { PackageName = packageName, SchemaScript = schemaScript };
        foreach (var table in targetTables)
        {
            var excluded = rows
                .Where(r => r.PackageName == packageName && NormalizeTableName(r.TargetTable) == NormalizeTableName(table))
                .Select(r => r.TargetColumn)
                .Where(c => c is not null)
                .Select(c => c!)
                .Distinct()
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();

            config.TargetTables.Add(new TargetTableConfig { Table = table, ExcludedColumns = excluded });
        }
        return config;
    }

    /// <summary>Strips SQL bracket-quoting ("[dbo].[Employee]" -> "dbo.Employee") -- the manifest writes bracketed names, this tool's own config uses plain ones.</summary>
    private static string NormalizeTableName(string? name) =>
        (name ?? "").Replace("[", "").Replace("]", "");

    public static void WriteToFile(PackageConfig config, string path)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(config, options));
    }

    public static PackageConfig ReadFromFile(string path) =>
        JsonSerializer.Deserialize<PackageConfig>(File.ReadAllText(path))
        ?? throw new InvalidOperationException($"could not deserialize config from {path}");
}
