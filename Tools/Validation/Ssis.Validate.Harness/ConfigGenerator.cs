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

/// <summary>One row of `ssisx report`'s primary-keys.json -- mirrors <c>Ssis.Extract.Model.Analysis.PrimaryKeyCandidateSpec</c>, same no-project-reference reasoning as <see cref="NonDeterministicRow"/>.</summary>
internal sealed class PrimaryKeyCandidateRow
{
    public string? PackageName { get; set; }
    public string? TargetTable { get; set; }
    public List<string>? Columns { get; set; }
    public string? Confidence { get; set; }
}

/// <summary>
/// Generates a starter <see cref="PackageConfig"/> from `ssisx report`'s non-determinism
/// manifest and primary-key candidates. <c>ExcludedColumns</c> is real, generated data.
/// <c>KeyColumn</c> is pre-filled ONLY when the extractor found exactly one candidate column
/// with non-"Unknown" confidence -- still a naming-convention guess, not a fact (see
/// <c>PrimaryKeyCandidateSpec</c>'s own doc comment for why a `.dtsx` can never be
/// authoritative here), so it is printed back with a reminder to confirm rather than silently
/// trusted. A composite or absent candidate leaves <c>KeyColumn</c> null exactly as before,
/// for a human to fill in (plan §12: "reviewed by a human").
/// </summary>
public static class ConfigGenerator
{
    public static PackageConfig Generate(string packageName, string schemaScript, List<string> targetTables, string nonDeterministicJsonPath, string? primaryKeysJsonPath = null)
    {
        var rows = File.Exists(nonDeterministicJsonPath)
            ? JsonSerializer.Deserialize<List<NonDeterministicRow>>(File.ReadAllText(nonDeterministicJsonPath)) ?? []
            : [];

        var pkCandidates = primaryKeysJsonPath is not null && File.Exists(primaryKeysJsonPath)
            ? JsonSerializer.Deserialize<List<PrimaryKeyCandidateRow>>(File.ReadAllText(primaryKeysJsonPath)) ?? []
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

            var candidate = pkCandidates.FirstOrDefault(c =>
                c.PackageName == packageName && NormalizeTableName(c.TargetTable) == NormalizeTableName(table)
                && c.Confidence != "Unknown" && c.Columns?.Count == 1);

            config.TargetTables.Add(new TargetTableConfig
            {
                Table = table,
                KeyColumn = candidate?.Columns?[0],
                ExcludedColumns = excluded,
            });

            if (candidate is not null)
            {
                Console.WriteLine($"{table}: KeyColumn auto-filled as '{candidate.Columns![0]}' ({candidate.Confidence}, from primary-keys.json) -- still confirm this before trusting it.");
            }
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
