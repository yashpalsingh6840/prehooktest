using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Summarizes what one pipeline component actually DOES, in one line, from its own bespoke
/// payload (<see cref="PipelineComponentSpec.Lookup"/>, <c>.ConditionalSplit</c>, <c>.MergeJoin</c>,
/// etc.) -- deliberately not the component's own <c>Description</c> property, which in every real
/// package checked so far is SSIS's own default boilerplate (equal to the component TYPE's
/// friendly name, e.g. "Flat File Destination"), never author-written insight.
///
/// Every raw enum decoded here (Lookup's NoMatchBehavior/CacheType, Merge Join's JoinType,
/// Aggregate's AggregationType) uses only the mapping this project already measured against real
/// SSIS -- see each payload type's own doc comment in <c>Ssis.Extract.Model</c>. An unmeasured raw
/// value is shown as the bare code (e.g. "Type=9"), never guessed at.
/// </summary>
public static class ComponentInsight
{
    public static string Describe(PipelineComponentSpec comp)
    {
        if (comp.FlatFileSource is { } ffs)
            return $"Reads flat file via `{ffs.ConnectionName}`" + (string.IsNullOrEmpty(ffs.FileNameColumnName) ? "" : $"; file name -> `{ffs.FileNameColumnName}`");

        if (comp.FlatFileDestination is { } ffd)
            return $"Writes flat file via `{ffd.ConnectionName}` ({(ffd.Overwrite == true ? "overwrites" : "appends")} each run)";

        if (comp.OleDbSource is { } os)
            return DescribeSqlRead(os.ConnectionName, os.OpenRowset, os.SqlCommand, "table");

        if (comp.OleDbDestination is { } od)
            return DescribeSqlWrite(od.ConnectionName, od.OpenRowset, od.SqlCommand, IsFastLoad(od));

        if (comp.AdoNetSource is { } aso)
            return DescribeSqlRead(aso.ConnectionName, aso.TableOrViewName, aso.SqlCommand, "table");

        if (comp.AdoNetDestination is { } ado)
            return $"Writes `{ado.TableOrViewName}` via `{ado.ConnectionName}`" + (ado.UseBulkInsertWhenPossible == true ? " (bulk insert)" : "");

        if (comp.ExcelSource is { } exs)
            return DescribeSqlRead(exs.ConnectionName, exs.OpenRowset, exs.SqlCommand, "worksheet");

        if (comp.OleDbCommand is { } cmd)
            return $"Runs `{Truncate(cmd.SqlCommand)}` once per row via `{cmd.ConnectionName}`";

        if (comp.Lookup is { } lk)
        {
            var parts = new List<string> { $"Looks up `{Truncate(lk.SqlCommand)}` via `{lk.ConnectionName}`" };
            if (lk.CacheTypeRaw == 0) parts.Add("full cache");
            else if (lk.CacheTypeRaw is { } c) parts.Add($"CacheType={c}");
            parts.Add(lk.NoMatchBehaviorRaw switch
            {
                1 => $"no-match -> `{lk.NoMatchOutputName}`",
                { } r => $"NoMatchBehavior={r}",
                null => "no-match behavior not set",
            });
            return string.Join("; ", parts);
        }

        if (comp.ConditionalSplit is { } cs)
        {
            var cases = cs.Cases
                .OrderBy(c => c.EvaluationOrder ?? int.MaxValue)
                .Select(c => $"{c.OutputName}: {Truncate(c.FriendlyExpression, 40)}");
            return $"{cs.Cases.Count} case(s) -- {string.Join("; ", cases)}; default -> `{cs.DefaultOutputName}`";
        }

        if (comp.DataConvert is { } dc)
        {
            var conversions = dc.Columns.Select(c => $"{c.OutputColumnName} -> {c.TargetDataType}");
            return $"{dc.Columns.Count} conversion(s): {string.Join("; ", conversions)}";
        }

        if (comp.Sort is { } sort)
        {
            var keys = sort.Keys.OrderBy(k => Math.Abs(k.Position)).Select(k => k.ColumnName);
            return $"Sorts by {string.Join(", ", keys)}";
        }

        if (comp.MergeJoin is { } mj)
        {
            var joinType = mj.JoinTypeRaw switch
            {
                0 => "FULL OUTER",
                1 => "LEFT OUTER",
                2 => "INNER",
                { } r => $"JoinType={r}",
                null => "join type not set",
            };
            return $"{joinType} join on {mj.NumKeyColumns} key column(s)";
        }

        if (comp.Aggregate is { } agg)
            return string.Join("; ", agg.Columns.Select(DescribeAggregateColumn));

        if (comp.RowCount is { } rc)
            return $"Writes row count to `{rc.VariableName}`";

        if (comp.ScriptComponent is { } sc)
        {
            var detail = $"Script ({sc.Language}): {sc.SourceFiles.Count} source file(s)";
            if (sc.ReadWriteVariables.Count > 0) detail += $"; writes {string.Join(", ", sc.ReadWriteVariables)}";
            return detail;
        }

        // Neither Multicast nor Merge/UnionAll carries a bespoke payload (unlike every type
        // above) -- there is nothing SSIS-specific to decode, only a real input/output count
        // already captured generically. Keyed on ComponentClassId, not a payload check, for
        // exactly that reason.
        if (comp.ComponentClassId is "Microsoft.Multicast")
        {
            // A Dangling output is a permanently-unconnected spare Microsoft.Multicast itself
            // auto-provisions the instant an existing output gets a path attached (see
            // PipelineOutputSpec.Dangling's own doc comment) -- counting it would overstate the
            // real fan-out by one.
            var outputs = comp.Outputs.Count(o => o.IsErrorOut != true && o.Dangling != true);
            return $"Fans out to {outputs} output(s)";
        }

        if (comp.ComponentClassId is "Microsoft.Merge" or "Microsoft.UnionAll")
            return $"Merges {comp.Inputs.Count} input(s) into one output";

        return "";
    }

    private static string DescribeAggregateColumn(AggregateColumnSpec c)
    {
        var fn = c.AggregationTypeRaw switch
        {
            0 => "GroupBy",
            1 => "Count",
            2 => "CountAll",
            3 => "CountDistinct",
            4 => "Sum",
            5 => "Average",
            6 => "Minimum",
            7 => "Maximum",
            { } r => $"Type={r}",
            null => "?",
        };
        return $"{c.OutputColumnName} = {fn}";
    }

    private static string DescribeSqlRead(string? connectionName, string? table, string? sql, string tableKind)
    {
        if (!string.IsNullOrEmpty(sql)) return $"Reads `{Truncate(sql)}` via `{connectionName}`";
        if (!string.IsNullOrEmpty(table)) return $"Reads {tableKind} `{table}` via `{connectionName}`";
        return $"Reads via `{connectionName}`";
    }

    private static string DescribeSqlWrite(string? connectionName, string? table, string? sql, bool fastLoad)
    {
        if (!string.IsNullOrEmpty(table)) return $"Writes `{table}` via `{connectionName}`" + (fastLoad ? " (fast load)" : "");
        if (!string.IsNullOrEmpty(sql)) return $"Writes via `{connectionName}`: `{Truncate(sql)}`";
        return $"Writes via `{connectionName}`";
    }

    private static bool IsFastLoad(OleDbDestinationPayload od) =>
        od.FastLoadOptions != null || od.FastLoadMaxInsertCommitSize != null || od.FastLoadKeepIdentity != null || od.FastLoadKeepNulls != null;

    /// <summary>Collapses embedded newlines (real SQL text in this portfolio is routinely
    /// multi-line, e.g. a `BEGIN TRANSACTION`/`TRY`/`CATCH` block) to a single space -- this
    /// string lands in a Markdown table cell, where a literal newline would break the table --
    /// and truncates so one long statement can't dominate the row.</summary>
    private static string Truncate(string? s, int max = 70)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var oneLine = s.Replace("\r", " ").Replace("\n", " ");
        while (oneLine.Contains("  ")) oneLine = oneLine.Replace("  ", " ");
        oneLine = oneLine.Trim();
        return oneLine.Length > max ? oneLine[..max] + "…" : oneLine;
    }
}
