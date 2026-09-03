using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Normalizes an OLE DB or ADO NET Destination/Source's own connection/table-name/fast-load
/// facts into one shape, so the rest of codegen (TryResolveEntityName, DbContextEmitter,
/// BuildSqlFlowSource, the fast-load gate) doesn't need to know which SSIS UI component
/// produced them -- an ADO NET Destination/Source is discriminated by
/// <see cref="PipelineComponentSpec.AdoNetDestination"/>/<see cref="PipelineComponentSpec.AdoNetSource"/>
/// being populated, not <see cref="PipelineComponentSpec.ComponentClassId"/> (that stays the
/// generic "Microsoft.ManagedComponentHost" -- see AdoNetDestinationPayload's own doc comment).
/// Added 2026-08-27 testing this tool against a real third-party portfolio
/// (SSIS_From_Sandeep's Package_Exports.dtsx, DFT_AdoNetRoundTrip).
/// </summary>
internal static class DestinationInfo
{
    public static string? ConnectionName(PipelineComponentSpec dest) =>
        dest.OleDbDestination?.ConnectionName ?? dest.AdoNetDestination?.ConnectionName;

    /// <summary>Bracket form ("[dbo].[Table]") regardless of which UI component produced it --
    /// an ADO NET Destination's own TableOrViewName is double-quoted ("dbo"."Table"),
    /// normalized here once so every existing OpenRowset-format parser (TryResolveEntityName,
    /// DbContextEmitter.ParseOpenRowset) keeps working unchanged.</summary>
    public static string? TableName(PipelineComponentSpec dest) =>
        dest.OleDbDestination?.OpenRowset ?? AdoNetSupport.NormalizeTableName(dest.AdoNetDestination?.TableOrViewName);

    /// <summary>An ADO NET Destination has no FastLoadMaxInsertCommitSize/FastLoadOptions --
    /// its own UseBulkInsertWhenPossible plays the equivalent role (observed true on the one
    /// real evidenced instance).</summary>
    public static bool IsFastLoadConfigured(PipelineComponentSpec dest) =>
        dest.OleDbDestination?.FastLoadMaxInsertCommitSize is not null
        || dest.OleDbDestination?.FastLoadOptions is not null
        || dest.AdoNetDestination?.UseBulkInsertWhenPossible == true;
}

internal static class SourceInfo
{
    /// <summary>True when this component is a SQL-based source of EITHER kind -- the one
    /// check PackagePlanner needs to decide whether a Data Flow Task has a wireable source at
    /// all, without caring which kind until BuildSqlFlowSource does.</summary>
    public static bool IsSqlSource(PipelineComponentSpec component) =>
        component.OleDbSource is not null || component.AdoNetSource is not null;

    public static string? ConnectionName(PipelineComponentSpec source) =>
        source.OleDbSource?.ConnectionName ?? source.AdoNetSource?.ConnectionName;
}

internal static class AdoNetSupport
{
    /// <summary>"dbo"."CustomerExportLog" -> "[dbo].[CustomerExportLog]" -- confirmed real
    /// double-quote shape from RBC_Demo_ETL's own ADO_DST_ExportLog, not assumed from
    /// documentation. Null (not a best-effort guess) when the text doesn't match this exact
    /// two-part quoted shape.</summary>
    public static string? NormalizeTableName(string? tableOrViewName)
    {
        if (string.IsNullOrEmpty(tableOrViewName)) return null;

        var parts = tableOrViewName.Split("\".\"", StringSplitOptions.None);
        if (parts.Length != 2) return null;

        var schema = parts[0].TrimStart('"');
        var table = parts[1].TrimEnd('"');
        if (schema.Length == 0 || table.Length == 0) return null;

        return $"[{schema}].[{table}]";
    }
}
