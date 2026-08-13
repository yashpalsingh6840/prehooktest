using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Cross-package dependency graph (plan §5.2). Two edge kinds, and the second is the point:
/// an Execute Package Task edge is an ordering dependency somebody wrote down, while a
/// shared-table edge (A writes <c>dbo.X</c>, B reads <c>dbo.X</c>) exists only in the
/// schedule and in nobody's documentation. On a real portfolio that second set is usually
/// the first time anyone has seen the true execution order.
/// </summary>
public static class DependencyGraphBuilder
{
    public static List<DependencyEdgeSpec> Build(List<PackageSpec> packages, Dictionary<string, DataTouchSpec> dataTouchByPackage)
    {
        var edges = new List<DependencyEdgeSpec>();

        // Explicit edges: Execute Package Task. Not present in either PoC package -- the
        // property names below are read generically from the task's own captured payload
        // rather than from a bespoke typed model, since this build slice has no real
        // example to model one against (same discipline as the pipeline component payloads).
        var packageNames = packages.Select(p => p.ObjectName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in packages)
        {
            foreach (var ex in PackageTree.AllExecutables(pkg))
            {
                if (ex.ExecutableType is not ("Microsoft.ExecutePackageTask" or "STOCK:ExecutePackageTask")) continue;

                // The child package reference lives inside the task's raw ObjectData XML.
                // Match it against known package names by substring rather than parsing a
                // shape we've never seen -- a miss here means no edge, never a wrong edge.
                var raw = ex.UnmappedTask?.RawObjectDataXml ?? "";
                foreach (var candidate in packageNames.Where(n => !n.Equals(pkg.ObjectName, StringComparison.OrdinalIgnoreCase)))
                {
                    if (raw.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        edges.Add(new DependencyEdgeSpec
                        {
                            FromPackage = pkg.ObjectName,
                            ToPackage = candidate,
                            Kind = "ExecutePackageTask",
                            SharedObject = null,
                            Detail = ex.RefId,
                        });
                    }
                }
            }
        }

        // Implicit edges: A writes a table B reads.
        foreach (var writer in packages)
        {
            if (!dataTouchByPackage.TryGetValue(writer.ObjectName, out var writerTouch)) continue;

            foreach (var reader in packages)
            {
                if (ReferenceEquals(writer, reader)) continue;
                if (!dataTouchByPackage.TryGetValue(reader.ObjectName, out var readerTouch)) continue;

                foreach (var table in writerTouch.TablesWritten)
                {
                    if (!readerTouch.TablesRead.Contains(table, StringComparer.OrdinalIgnoreCase)) continue;

                    edges.Add(new DependencyEdgeSpec
                    {
                        FromPackage = writer.ObjectName,
                        ToPackage = reader.ObjectName,
                        Kind = "SharedTable",
                        SharedObject = table,
                        Detail = $"{writer.ObjectName} writes {table}; {reader.ObjectName} reads it",
                    });
                }
            }
        }

        return edges
            .OrderBy(e => e.FromPackage, StringComparer.Ordinal)
            .ThenBy(e => e.ToPackage, StringComparer.Ordinal)
            .ThenBy(e => e.SharedObject ?? "", StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Renders the portfolio graph as Mermaid (plan §6.1's <c>graph/portfolio.mmd</c>). Explicit edges are solid, implicit shared-table edges dotted and labelled with the table -- the visual distinction matters because only one of the two kinds is written down anywhere.</summary>
    public static string ToMermaid(List<PackageSpec> packages, List<DependencyEdgeSpec> edges)
    {
        var ids = packages
            .Select(p => p.ObjectName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select((name, i) => (name, id: $"p{i}"))
            .ToDictionary(x => x.name, x => x.id, StringComparer.OrdinalIgnoreCase);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("flowchart LR");
        foreach (var (name, id) in ids.OrderBy(kv => kv.Value, StringComparer.Ordinal))
        {
            sb.AppendLine($"    {id}[\"{name.Replace("\"", "'")}\"]");
        }

        foreach (var e in edges)
        {
            if (!ids.TryGetValue(e.FromPackage, out var from) || !ids.TryGetValue(e.ToPackage, out var to)) continue;
            sb.AppendLine(e.Kind == "SharedTable"
                ? $"    {from} -.->|\"{e.SharedObject}\"| {to}"
                : $"    {from} --> {to}");
        }

        if (edges.Count == 0)
        {
            sb.AppendLine("    %% no cross-package dependencies found");
        }

        return sb.ToString();
    }
}
