using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;

namespace Svk.Core;

/// <summary>
/// Discovers every source, destination and Lookup in a package by scanning which bespoke
/// payload is populated on each pipeline component -- covers every source/destination type
/// this extractor already models (Flat File, Excel, OLE DB, ADO NET). Deliberately walks
/// PipelineComponentSpec directly rather than Ssis.Extract.Codegen's PackagePlanner: that
/// planner is scoped to the single-source/single-destination shapes `ssisx generate` can turn
/// into C#, with a reported gap for everything else -- sample data needs every touchpoint
/// regardless of whether the flow shape is one `ssisx generate` can wire up.
/// </summary>
public static class SampleDataPlanner
{
    public static PackagePlan Plan(PackageSpec package)
    {
        var allExecutables = PackageTree.AllExecutables(package).ToList();
        var primaryKeys = PrimaryKeyInference.Infer(package, allExecutables)
            .GroupBy(k => k.DestinationComponentRefId)
            .ToDictionary(g => g.Key, g => g.First());
        var connectionManagersByName = package.ConnectionManagers
            .GroupBy(cm => cm.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var sources = new List<SourceTouchPoint>();
        var destinations = new List<DestinationTouchPoint>();
        var lookups = new List<LookupTouchPoint>();

        foreach (var ex in allExecutables.Where(e => e.DataFlowTask is not null))
        {
            foreach (var comp in ex.DataFlowTask!.Pipeline.Components)
            {
                if (comp.FlatFileSource is not null)
                {
                    var format = comp.FlatFileSource.ConnectionName is { } cmName
                        && connectionManagersByName.TryGetValue(cmName, out var cm)
                        ? cm.FlatFileFormat
                        : null;
                    sources.Add(BuildSource(comp, TouchPointKind.FlatFileSource, null, format));
                }
                if (comp.ExcelSource is not null)
                {
                    var hasHeaderRow = ResolveExcelHasHeaderRow(comp.ExcelSource.ConnectionName, connectionManagersByName);
                    sources.Add(BuildSource(comp, TouchPointKind.ExcelSource, comp.ExcelSource.OpenRowset, excelHasHeaderRow: hasHeaderRow));
                }
                if (comp.OleDbSource is not null)
                    sources.Add(BuildSource(comp, TouchPointKind.SqlSource, comp.OleDbSource.OpenRowset));
                if (comp.AdoNetSource is not null)
                    sources.Add(BuildSource(comp, TouchPointKind.SqlSource, comp.AdoNetSource.TableOrViewName));

                if (comp.OleDbDestination is not null)
                    destinations.Add(BuildDestination(comp, TouchPointKind.SqlDestination, comp.OleDbDestination.OpenRowset, primaryKeys));
                if (comp.AdoNetDestination is not null)
                    destinations.Add(BuildDestination(comp, TouchPointKind.SqlDestination, comp.AdoNetDestination.TableOrViewName, primaryKeys));
                if (comp.FlatFileDestination is not null)
                    destinations.Add(BuildDestination(comp, TouchPointKind.FlatFileDestination, null, primaryKeys));

                if (comp.Lookup is not null)
                {
                    var joinKey = LookupJoinKey.TryDerive(comp);
                    var refColumns = comp.Lookup.ReferenceColumns
                        .Select(c => new ColumnSchema(c.Name, NormalizeLookupDataType(c.DataType), c.Length, c.Precision, c.Scale))
                        .ToList();
                    lookups.Add(new LookupTouchPoint(comp.Name, comp.RefId, comp.Lookup.SqlCommand, refColumns, joinKey));
                }
            }
        }

        return new PackagePlan { PackageName = package.ObjectName, Sources = sources, Destinations = destinations, Lookups = lookups };
    }

    private static SourceTouchPoint BuildSource(PipelineComponentSpec comp, TouchPointKind kind, string? target, FlatFileFormatSpec? format = null, bool? excelHasHeaderRow = null)
    {
        var output = comp.Outputs.FirstOrDefault(o => o.IsErrorOut != true);
        var fallback = output?.Columns.Select(c => new ColumnSchema(c.Name, c.DataType, c.Length, c.Precision, c.Scale)).ToList();
        var schema = ResolveSchema(output?.ExternalMetadataColumns, fallback);
        return new SourceTouchPoint(comp.Name, comp.RefId, kind, schema, target, format, excelHasHeaderRow);
    }

    /// <summary>Mirrors Ssis.Extract.Codegen.PackageGenerator.BuildExcelFlowSource's own HDR
    /// detection exactly, deliberately DUPLICATED rather than referenced -- SvkCore's own
    /// governance boundary (see SvkCore.csproj) forbids a ProjectReference to
    /// Ssis.Extract.Codegen. Keep both in sync by hand if the detection rule ever changes. The
    /// EXCEL connection manager's own ACE OLEDB connection string embeds "Extended
    /// Properties=...HDR=YES" -- landed as its own Extras["HDR"] entry (with a trailing stray
    /// '"') by the generic, quote-unaware ';'-split every OLEDB-shaped connection string already
    /// gets parsed through. Absent entirely, true is the one evidenced real default (HDR=YES),
    /// not a guess.</summary>
    private static bool ResolveExcelHasHeaderRow(string? connectionName, Dictionary<string, ConnectionManagerSpec> connectionManagers)
    {
        if (connectionName is null || !connectionManagers.TryGetValue(connectionName, out var cm)) return true;
        var hdrEntry = cm.Parsed?.Extras.FirstOrDefault(kv => kv.Key.Equals("HDR", StringComparison.OrdinalIgnoreCase));
        if (hdrEntry is { Value.Length: > 0 } found)
            return found.Value.TrimEnd('"').Equals("YES", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static DestinationTouchPoint BuildDestination(PipelineComponentSpec comp, TouchPointKind kind, string? tableName, Dictionary<string, PrimaryKeyCandidateSpec> primaryKeys)
    {
        var input = comp.Inputs.FirstOrDefault();
        var fallback = input?.Columns.Select(c => new ColumnSchema(c.CachedName, c.CachedDataType, c.CachedLength, c.CachedPrecision, c.CachedScale)).ToList();
        var schema = ResolveSchema(input?.ExternalMetadataColumns, fallback);
        primaryKeys.TryGetValue(comp.RefId, out var pk);
        return new DestinationTouchPoint(comp.Name, comp.RefId, kind, schema, tableName, pk);
    }

    /// <summary>A Lookup's own ReferenceColumns (parsed from ReferenceMetadataXml, not the
    /// ordinary externalMetadataColumns mechanism) carry the DT_-prefixed type spelling
    /// ("DT_WSTR") rather than the short buffer form ("wstr") SsisPipelineTypeMap is keyed by
    /// -- normalized here at the one call site that reads it, rather than widening the shared
    /// map's accepted vocabulary for every other consumer.</summary>
    private static string? NormalizeLookupDataType(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        return raw.StartsWith("DT_", StringComparison.OrdinalIgnoreCase) ? raw[3..].ToLowerInvariant() : raw.ToLowerInvariant();
    }

    /// <summary>Prefers the design-time external-metadata contract (the FULL declared file/table
    /// schema, per PipelineExternalMetadataColumnSpec's own doc comment) over the wired
    /// input/output columns, which only cover what's actually mapped to a pipeline column.</summary>
    private static List<ColumnSchema> ResolveSchema(List<PipelineExternalMetadataColumnSpec>? external, List<ColumnSchema>? fallback)
    {
        if (external is { Count: > 0 })
        {
            return external.Select(c => new ColumnSchema(c.Name, c.DataType, c.Length, c.Precision, c.Scale)).ToList();
        }
        return fallback ?? [];
    }
}
