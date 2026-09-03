using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Best-effort primary-key detection for gate 3's <c>KeyColumn</c> (see
/// <see cref="Ssis.Extract.Model.Analysis.PrimaryKeyCandidateSpec"/>'s doc comment for why a
/// `.dtsx` alone can never carry an authoritative answer). Two signals, applied to a
/// destination's own input columns in their declared order:
///
/// 1. A column whose name matches "&lt;last segment of the target table&gt;ID" (e.g.
///    "[dbo].[Employee]" -&gt; "EmployeeID") -- the dominant real-world convention, and what
///    all three of this PoC's own target tables actually use (EmployeeID, DepartmentID,
///    DesignationID, verified against the live database's real PRIMARY KEY constraints).
/// 2. Failing that, a column literally named "ID" -- the other common convention, not
///    evidenced in this PoC (every table here qualifies the ID with its own name) but common
///    enough elsewhere to be worth a fallback.
///
/// A match only counts if the column is (a) an integer pipeline type ("i1"/"i2"/"i4"/"i8" --
/// the literal short codes this PoC's own `.dtsx` files use, e.g. <c>dataType="i4"</c> on
/// EmployeeID, not a numeric enum -- see SsisTypeCodeMaps' own doc comment on why these are a
/// distinct space from the DTS:DataType enum) and (b) NOT a computed value -- traced via the
/// same lineageId join <c>LineageBuilder</c> uses, so a derived business key like
/// "EmployeeKey" (a string concatenation) never gets mistaken for the surrogate integer key.
/// Neither signal needs a live database; both are wrong often enough that every result is
/// reported with its reasoning and still needs a human to confirm it (plan §12's own framing
/// for gate 3's <c>KeyColumn</c>).
/// </summary>
public static class PrimaryKeyInference
{
    private static readonly HashSet<string> IntegerPipelineTypes = new(StringComparer.Ordinal) { "i1", "i2", "i4", "i8" };

    public static List<PrimaryKeyCandidateSpec> Infer(PackageSpec package, List<ExecutableSpec> allExecutables)
    {
        var results = new List<PrimaryKeyCandidateSpec>();

        foreach (var ex in allExecutables.Where(e => e.DataFlowTask is not null))
        {
            // ADO NET Destination (Microsoft.ADONETDestination, discriminated by payload not
            // ComponentClassId -- see AdoNetDestinationPayload's own doc comment) included
            // alongside OLE DB Destination, added 2026-08-27 testing this tool against a real
            // third-party portfolio (SSIS_From_Sandeep). TableOrViewName is double-quoted
            // ("dbo"."Table"), not bracketed like OpenRowset -- LastSegment below already
            // trims on '.'/'[' /']', which happens not to need a '"' case: TrimEnd('"') isn't
            // applied here, so an ADO NET target's LastSegment would keep a trailing quote --
            // handled by normalizing to bracket form first via the same helper codegen uses.
            foreach (var comp in ex.DataFlowTask!.Pipeline.Components.Where(c => c.OleDbDestination is not null || c.AdoNetDestination is not null))
            {
                var target = comp.OleDbDestination?.OpenRowset ?? NormalizeAdoNetTableName(comp.AdoNetDestination?.TableOrViewName);
                var mainInput = comp.Inputs.FirstOrDefault();
                if (mainInput is null) continue;

                var producerExpressionByLineageId = ex.DataFlowTask.Pipeline.Components
                    .SelectMany(c => c.Outputs)
                    .SelectMany(o => o.Columns)
                    .GroupBy(c => c.LineageId)
                    .ToDictionary(g => g.Key, g => g.First().Expression, StringComparer.Ordinal);

                var candidateName = target is null ? null : LastSegment(target) + "ID";

                PipelineInputColumnSpec? match = null;
                string? matchReason = null;

                foreach (var col in mainInput.Columns)
                {
                    var isDerived = producerExpressionByLineageId.TryGetValue(col.LineageId, out var expr) && expr is not null;
                    var isIntegerType = col.CachedDataType is not null && IntegerPipelineTypes.Contains(col.CachedDataType);
                    if (isDerived || !isIntegerType) continue;

                    if (candidateName is not null && string.Equals(col.CachedName, candidateName, StringComparison.OrdinalIgnoreCase))
                    {
                        match = col;
                        matchReason = $"column '{col.CachedName}' matches the naming convention \"<table>ID\" for target table {target}, is an integer pipeline type ({col.CachedDataType}), and is not a computed/derived value";
                        break;
                    }
                    if (match is null && string.Equals(col.CachedName, "ID", StringComparison.OrdinalIgnoreCase))
                    {
                        match = col;
                        matchReason = $"column '{col.CachedName}' matches the fallback naming convention \"ID\", is an integer pipeline type ({col.CachedDataType}), and is not a computed/derived value";
                    }
                }

                results.Add(new PrimaryKeyCandidateSpec
                {
                    PackageName = package.ObjectName,
                    DataFlowTaskPath = ex.RefId,
                    DestinationComponentName = comp.Name,
                    DestinationComponentRefId = comp.RefId,
                    TargetTable = target,
                    Columns = match is not null ? [match.CachedName] : [],
                    Confidence = match is not null ? "NamingConvention" : "Unknown",
                    Reason = matchReason ?? $"no column name matched \"<table>ID\"/\"ID\" among an integer, non-derived column for target table {target ?? "(unknown -- destination uses a SQL command)"}",
                });
            }
        }

        return results;
    }

    private static string LastSegment(string bracketedOrPlainTableName)
    {
        var normalized = bracketedOrPlainTableName.Replace("[", "").Replace("]", "");
        var parts = normalized.Split('.');
        return parts[^1];
    }

    /// <summary>"dbo"."CustomerExportLog" -> "[dbo].[CustomerExportLog]" -- same normalization
    /// Ssis.Extract.Codegen.AdoNetSupport.NormalizeTableName does, duplicated here rather than
    /// shared since Ssis.Extract.Dtsx has no dependency on Ssis.Extract.Codegen (the direction
    /// runs the other way) and this is an 8-line helper, not worth a new shared project just for
    /// two call sites.</summary>
    private static string? NormalizeAdoNetTableName(string? tableOrViewName)
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
