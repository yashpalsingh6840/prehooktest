using System.Text.Json;
using Ssis.Validate.Harness;

namespace Ssis.Validate.Harness.Tests;

/// <summary>
/// Pure-logic coverage of <see cref="SnapshotDiffer"/> -- no database needed. Written after
/// noticing the self-test job's own end-to-end "pass" run (docs/gate3-harness.md) never
/// actually exercised the non-determinism EXCLUSION path: that job copies the golden
/// snapshot's own captured values verbatim, so an excluded column like LoadedAtUtc is
/// identical between expected and actual by construction, not because exclusion was doing
/// anything. These tests close that gap directly.
/// </summary>
public class SnapshotDifferTests
{
    private static JsonElement E(object v) => v switch
    {
        string s => JsonSerializer.SerializeToElement(s),
        int i => JsonSerializer.SerializeToElement(i),
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    private static TableSnapshot Snapshot(params (int Id, string Name, string Stamp)[] rows) => new()
    {
        Table = "dbo.T",
        Columns = ["Id", "Name", "Stamp"],
        Rows = rows.Select(r => new List<JsonElement?> { E(r.Id), E(r.Name), E(r.Stamp) }).ToList(),
    };

    [Fact]
    public void IdenticalSnapshots_ProduceNoDifferences()
    {
        var expected = Snapshot((1, "A", "t1"), (2, "B", "t2"));
        var actual = Snapshot((1, "A", "t1"), (2, "B", "t2"));

        var diff = SnapshotDiffer.Diff(expected, actual, "Id", new HashSet<string>());

        Assert.True(diff.IsMatch);
        Assert.Empty(diff.Differences);
    }

    [Fact]
    public void DifferingExcludedColumn_IsNotReported_EvenThoughValuesDiffer()
    {
        // Same business data, but "Stamp" (e.g. LoadedAtUtc) genuinely differs -- exactly
        // what a non-deterministic GETUTCDATE() column looks like on two separate runs.
        var expected = Snapshot((1, "A", "2026-01-01T00:00:00Z"));
        var actual = Snapshot((1, "A", "2026-06-15T12:34:56Z"));

        var diff = SnapshotDiffer.Diff(expected, actual, "Id", new HashSet<string> { "Stamp" });

        Assert.True(diff.IsMatch);
    }

    [Fact]
    public void DifferingExcludedColumn_IsCaughtIfNotActuallyExcluded()
    {
        // Same inputs as above, but WITHOUT excluding "Stamp" -- proves the previous test's
        // pass is because of the exclusion, not because the differ ignores that column by
        // default or the test data happened to match.
        var expected = Snapshot((1, "A", "2026-01-01T00:00:00Z"));
        var actual = Snapshot((1, "A", "2026-06-15T12:34:56Z"));

        var diff = SnapshotDiffer.Diff(expected, actual, "Id", new HashSet<string>());

        Assert.False(diff.IsMatch);
        Assert.Single(diff.Differences);
        Assert.Equal("Changed", diff.Differences[0].Kind);
        Assert.Contains("Stamp", diff.Differences[0].ChangedColumns);
    }

    [Fact]
    public void ChangedNonExcludedColumn_IsReportedWithExpectedAndActualValues()
    {
        var expected = Snapshot((1, "A", "t1"));
        var actual = Snapshot((1, "B", "t1"));

        var diff = SnapshotDiffer.Diff(expected, actual, "Id", new HashSet<string>());

        Assert.False(diff.IsMatch);
        var d = Assert.Single(diff.Differences);
        Assert.Equal("Changed", d.Kind);
        Assert.Equal(("A", "B"), d.ChangedValues["Name"]);
    }

    [Fact]
    public void RowMissingFromActual_IsReportedAsMissing()
    {
        var expected = Snapshot((1, "A", "t1"), (2, "B", "t2"));
        var actual = Snapshot((1, "A", "t1"));

        var diff = SnapshotDiffer.Diff(expected, actual, "Id", new HashSet<string>());

        Assert.False(diff.IsMatch);
        var d = Assert.Single(diff.Differences);
        Assert.Equal("Missing", d.Kind);
    }

    [Fact]
    public void UnexpectedRowInActual_IsReportedAsExtra()
    {
        var expected = Snapshot((1, "A", "t1"));
        var actual = Snapshot((1, "A", "t1"), (2, "B", "t2"));

        var diff = SnapshotDiffer.Diff(expected, actual, "Id", new HashSet<string>());

        Assert.False(diff.IsMatch);
        var d = Assert.Single(diff.Differences);
        Assert.Equal("Extra", d.Kind);
    }

    [Fact]
    public void RowCountMismatchAloneFailsIsMatch_EvenWithNoPerRowDifferences()
    {
        // ExpectedRowCount != ActualRowCount must fail the gate even in a pathological case
        // where every row that IS present happens to match -- IsMatch checks both, not just
        // Differences.Count == 0.
        var result = new TableDiffResult { Table = "dbo.T", ExpectedRowCount = 2, ActualRowCount = 1 };
        Assert.False(result.IsMatch);
    }
}
