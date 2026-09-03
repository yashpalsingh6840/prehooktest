using System.Text.Json;

namespace Ssis.Validate.Harness;

public sealed class RowDiff
{
    public required string Kind { get; init; } // "Missing" (expected but not in actual), "Extra" (in actual but not expected), "Changed"
    public required object? Key { get; init; }
    public List<string> ChangedColumns { get; init; } = [];
    public Dictionary<string, (string? Expected, string? Actual)> ChangedValues { get; init; } = [];
}

public sealed class TableDiffResult
{
    public required string Table { get; init; }
    public required int ExpectedRowCount { get; init; }
    public required int ActualRowCount { get; init; }
    public List<RowDiff> Differences { get; init; } = [];
    public bool IsMatch => Differences.Count == 0 && ExpectedRowCount == ActualRowCount;
}

/// <summary>
/// Compares an "after" snapshot from the golden corpus against the actual result of running
/// the job under test, excluding columns named in the package's non-determinism manifest
/// (plan §5's "non-determinism is handled automatically" -- the same manifest
/// <c>ssisx report</c> already produces at gate 0/1, not a second exclusion list to maintain).
/// </summary>
public static class SnapshotDiffer
{
    public static TableDiffResult Diff(TableSnapshot expected, TableSnapshot actual, string keyColumn, IReadOnlySet<string> excludedColumns)
    {
        if (expected.Table != actual.Table)
            throw new InvalidOperationException($"comparing snapshots of different tables: {expected.Table} vs {actual.Table}");

        var comparedColumns = expected.Columns.Where(c => !excludedColumns.Contains(c)).ToList();
        var keyIndexExpected = expected.Columns.IndexOf(keyColumn);
        var keyIndexActual = actual.Columns.IndexOf(keyColumn);
        if (keyIndexExpected < 0 || keyIndexActual < 0)
            throw new InvalidOperationException($"key column '{keyColumn}' not found in snapshot for {expected.Table}");

        var expectedByKey = expected.Rows.ToDictionary(r => KeyText(r[keyIndexExpected]));
        var actualByKey = actual.Rows.ToDictionary(r => KeyText(r[keyIndexActual]));

        var diffs = new List<RowDiff>();

        foreach (var (key, expectedRow) in expectedByKey)
        {
            if (!actualByKey.TryGetValue(key, out var actualRow))
            {
                diffs.Add(new RowDiff { Kind = "Missing", Key = key });
                continue;
            }

            var changed = new List<string>();
            var changedValues = new Dictionary<string, (string?, string?)>();
            foreach (var col in comparedColumns)
            {
                var ei = expected.Columns.IndexOf(col);
                var ai = actual.Columns.IndexOf(col);
                if (ai < 0)
                {
                    changed.Add(col);
                    changedValues[col] = (Text(expectedRow[ei]), "<column missing from actual result>");
                    continue;
                }
                var ev = Text(expectedRow[ei]);
                var av = Text(actualRow[ai]);
                if (ev != av)
                {
                    changed.Add(col);
                    changedValues[col] = (ev, av);
                }
            }
            if (changed.Count > 0)
            {
                diffs.Add(new RowDiff { Kind = "Changed", Key = key, ChangedColumns = changed, ChangedValues = changedValues });
            }
        }

        foreach (var key in actualByKey.Keys.Except(expectedByKey.Keys))
        {
            diffs.Add(new RowDiff { Kind = "Extra", Key = key });
        }

        return new TableDiffResult
        {
            Table = expected.Table,
            ExpectedRowCount = expected.Rows.Count,
            ActualRowCount = actual.Rows.Count,
            Differences = diffs,
        };
    }

    private static string KeyText(JsonElement? e) => Text(e) ?? "<null>";

    private static string? Text(JsonElement? e) => e switch
    {
        null => null,
        { ValueKind: JsonValueKind.Null } => null,
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        var v => v.Value.GetRawText(),
    };
}
