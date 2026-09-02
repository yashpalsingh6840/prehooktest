using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Covers <c>DTS:EventHandler</c> reporting. Event handlers were extracted all along
/// (<c>PackageSpec.EventHandlers</c>, <c>ExecutableSpec.EventHandlers</c>) and
/// <b>Ssis.Extract.Codegen referenced them nowhere at all</b> -- grep-confirmed, zero hits
/// before the 2026-09-02 gap audit. So a package with an OnError handler generated as though it
/// had none, in silence, and still counted as generatable.
///
/// <para>That was live, not theoretical: the third-party Package_Advanced.dtsx carries an OnError
/// handler containing an Execute SQL Task (SQL_LogOnError), so real SSIS writes a log row on any
/// error and the generated job does not. Reporting it moves that package out of the generatable
/// set (4/5 -> 3/5 by the digest's own criterion), which is the point -- the count was wrong,
/// not the package.</para>
///
/// <para>Built in memory rather than from a fixture on purpose: there is no SSIS runtime
/// behaviour to measure here, only reporting logic, and three branches to pin (contains work /
/// contains nothing / disabled) that a single .dtsx cannot express at once.</para>
/// </summary>
public class EventHandlerReportingTests
{
    private static PackageSpec Package(params EventHandlerSpec[] handlers) => new()
    {
        ObjectName = "PkgWithHandlers",
        SourceDtsxPath = "in-memory.dtsx",
        Sha256 = new string('0', 64),
        FileSizeBytes = 0,
        LastWriteTimeUtc = DateTime.UnixEpoch,
        ProtectionLevelRaw = 0,
        ProtectionLevelName = "DontSaveSensitive",
        Coverage = new CoverageStats { TotalElements = 0, UnmappedElements = 0, ExcludedElements = 0, CoveragePercent = 100 },
        EventHandlers = [.. handlers],
    };

    private static ExecutableSpec SqlTask(string name, bool disabled = false) => new()
    {
        RefId = $"Package.EventHandlers[OnError].Tasks[{name}]",
        ExecutableType = "Microsoft.ExecuteSQLTask",
        ObjectName = name,
        Disabled = disabled ? true : null,
        ExecuteSqlTask = new ExecuteSqlTaskPayload { SqlStatementSource = "INSERT INTO dbo.ErrorLog DEFAULT VALUES;" },
    };

    private static EventHandlerSpec Handler(string eventName, bool disabled, params ExecutableSpec[] children) => new()
    {
        EventName = eventName,
        RefId = $"Package.EventHandlers[{eventName}]",
        Disabled = disabled ? true : null,
        Children = [.. children],
    };

    [Fact]
    public void Plan_ReportsABlockingGap_ForAnEventHandlerThatContainsWork()
    {
        // The real Package_Advanced shape: OnError -> one Execute SQL Task.
        var plan = PackagePlanner.Plan(Package(Handler("OnError", disabled: false, SqlTask("SQL_LogOnError"))));

        var gap = Assert.Single(plan.Gaps, g => g.Location.Contains("EventHandlers[OnError]", StringComparison.Ordinal));
        Assert.True(gap.IsBlocking);
        Assert.Contains("SQL_LogOnError", gap.Reason);
        // The reason must say why it is not simply translated as a failure handler.
        Assert.Contains("not the same as the Failure-constraint handler", gap.Reason);
    }

    [Fact]
    public void Plan_SaysNothing_ForAnEventHandlerThatContainsNoWork()
    {
        // SSDT creates an empty handler (holding only the stock Propagate variable) the moment
        // anyone clicks the handler tab. It runs nothing, so reporting it would be pure noise --
        // and noise is what trains people to ignore the gap list.
        var plan = PackagePlanner.Plan(Package(Handler("OnPreExecute", disabled: false)));

        Assert.DoesNotContain(plan.Gaps, g => g.Location.Contains("EventHandlers[", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_ReportsOnlyAnAdvisory_WhenTheHandlerIsDisabled()
    {
        // A disabled handler does not run under SSIS either, so generated code omitting it
        // MATCHES -- non-blocking, but still stated, so the difference is never silent.
        var plan = PackagePlanner.Plan(Package(Handler("OnError", disabled: true, SqlTask("SQL_LogOnError"))));

        var gap = Assert.Single(plan.Gaps, g => g.Location.Contains("EventHandlers[OnError]", StringComparison.Ordinal));
        Assert.False(gap.IsBlocking);
        Assert.Contains("disabled", gap.Reason);
    }

    [Fact]
    public void Plan_IgnoresADisabledTaskInsideAnOtherwiseLiveHandler()
    {
        // A handler whose only child is disabled runs nothing, exactly like an empty one.
        var plan = PackagePlanner.Plan(Package(Handler("OnError", disabled: false, SqlTask("SQL_LogOnError", disabled: true))));

        Assert.DoesNotContain(plan.Gaps, g => g.Location.Contains("EventHandlers[", StringComparison.Ordinal));
    }
}
