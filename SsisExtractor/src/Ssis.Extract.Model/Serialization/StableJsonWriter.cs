using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ssis.Extract.Model.Serialization;

/// <summary>
/// Serializes a spec POCO to deterministic JSON (plan §2.3): object keys sorted
/// alphabetically at every level, so a `git diff` on the output is a genuine package
/// change detector rather than noise from property-declaration-order or dictionary
/// iteration order. Array element ORDER is preserved as-is -- it usually reflects
/// document order in the source XML (e.g. flat-file column order), which is itself
/// deterministic for a given input file and is meaningful, unlike object key order.
/// </summary>
public static class StableJsonWriter
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    public static string ToStableJson<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, SerializeOptions);
        var sorted = SortKeys(node);
        // Trailing newline: clean diffs, POSIX-friendly files.
        return sorted!.ToJsonString(WriteOptions) + "\n";
    }

    public static void WriteToFile<T>(T value, string path)
    {
        var json = ToStableJson(value);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        // No BOM: the golden-file tests compare bytes, and a BOM is one more thing that
        // silently differs between "generated fresh" and "checked into git" depending on
        // which tool touched it last.
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static JsonNode? SortKeys(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var entries = obj.ToList();
                foreach (var (key, _) in entries)
                {
                    obj.Remove(key);
                }
                foreach (var (key, child) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    obj.Add(key, SortKeys(child));
                }
                return obj;

            case JsonArray arr:
                var items = arr.ToList();
                arr.Clear();
                foreach (var item in items)
                {
                    arr.Add(SortKeys(item));
                }
                return arr;

            default:
                return node;
        }
    }
}
