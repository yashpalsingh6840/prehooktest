using System.Text.Json;
using Ssis.Extract.Model.Package;

namespace Svk.Core;

/// <summary>
/// Reads the spec.json files an unmodified `ssisx extract` run already wrote --
/// this tool's only input. Deserializes with plain default JsonSerializerOptions:
/// spec.json's own writer (Ssis.Extract.Model.Serialization.StableJsonWriter) applies no
/// naming policy, so property names in the JSON already match the C# property names exactly.
/// </summary>
public static class PackageSpecLoader
{
    public static List<PackageSpec> Load(string specDir, IReadOnlyList<string>? packageNames = null)
    {
        var packagesDir = Path.Combine(specDir, "packages");
        if (!Directory.Exists(packagesDir))
        {
            throw new DirectoryNotFoundException(
                $"No 'packages' folder under '{specDir}' -- run 'ssisx extract --input <dtsx-folder> --out {specDir}' first.");
        }

        var files = Directory.GetFiles(packagesDir, "*.spec.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
        var wanted = packageNames is { Count: > 0 } ? new HashSet<string>(packageNames, StringComparer.OrdinalIgnoreCase) : null;

        var result = new List<PackageSpec>();
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var name = fileName.EndsWith(".spec.json", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^".spec.json".Length]
                : Path.GetFileNameWithoutExtension(fileName);
            if (wanted is not null && !wanted.Contains(name)) continue;

            var pkg = JsonSerializer.Deserialize<PackageSpec>(File.ReadAllText(file))
                      ?? throw new InvalidDataException($"'{file}' did not deserialize to a PackageSpec.");
            result.Add(pkg);
        }

        if (wanted is not null)
        {
            var found = new HashSet<string>(result.Select(p => p.ObjectName), StringComparer.OrdinalIgnoreCase);
            foreach (var missing in wanted.Where(w => !found.Contains(w)))
            {
                Console.Error.WriteLine($"warning: --package '{missing}' matched no package under '{packagesDir}'.");
            }
        }

        return result;
    }
}
