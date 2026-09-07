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
    private static PackageSpec Package(params EventHandlerSpec[] handlers) => Package(null, handlers);

    private static PackageSpec Package(ExecutionSemanticsSpec? semantics, params EventHandlerSpec[] handlers) => new()
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
        ExecutionSemantics = semantics ?? new ExecutionSemanticsSpec(),
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
    public void Plan_TranslatesAPackageRootOnErrorHandler_WithASingleExecuteSqlTask()
    {
        // The real Package_Advanced shape: OnError -> one Execute SQL Task. Superseded
        // 2026-09-03: a real dtexec probe (SyntheticEventHandlerProbe.dtsx) measured that, under
        // the default MaximumErrorCount=1 everywhere, "some task failed" and "the whole package
        // failed" are the same event -- so this shape is now translated as a failure handler
        // (the generated Program.cs already runs failure handlers unconditionally on any
        // exception), not reported as an untranslatable gap.
        var plan = PackagePlanner.Plan(Package(Handler("OnError", disabled: false, SqlTask("SQL_LogOnError"))));

        Assert.DoesNotContain(plan.Gaps, g => g.Location.Contains("EventHandlers[OnError]", StringComparison.Ordinal));
        var handler = Assert.Single(plan.FailureHandlers);
        Assert.Equal("SQL_LogOnError", handler.TaskName);
        Assert.Equal("INSERT INTO dbo.ErrorLog DEFAULT VALUES;", handler.Sql);
    }

    [Fact]
    public void Plan_ReportsABlockingGap_ForAnOnErrorHandler_WhenMaximumErrorCountIsRaised()
    {
        // The precondition ResolveErrorEventHandler's own doc comment names: raising
        // MaximumErrorCount anywhere in the tree is exactly what the probe measured as letting a
        // task fail while the package as a whole still succeeds -- a state this generator's single
        // whole-package transaction cannot represent, so it must gap rather than guess.
        var package = Package(new ExecutionSemanticsSpec { MaximumErrorCount = 5 },
            Handler("OnError", disabled: false, SqlTask("SQL_LogOnError")));
        var plan = PackagePlanner.Plan(package);

        var gap = Assert.Single(plan.Gaps, g => g.Location.Contains("EventHandlers[OnError]", StringComparison.Ordinal));
        Assert.True(gap.IsBlocking);
        Assert.Contains("MaximumErrorCount", gap.Reason);
        Assert.Empty(plan.FailureHandlers);
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

    [Fact]
    public void Plan_ReportsABlockingGap_ForAnOnErrorHandlerOnANestedContainer()
    {
        // ResolveErrorEventHandler's own doc comment states this is not an oversight: under the
        // measured MaximumErrorCount=1-everywhere precondition, a handler below the package root
        // could only ever fire on a run where nothing above it also failed -- which this rewrite's
        // whole-package-abort model already treats as "the run succeeded" -- so it is a genuinely
        // different, unevidenced shape from the one real evidenced case, not a generalization of it.
        var seq = new ExecutableSpec
        {
            RefId = "Package\\SEQ_A",
            ObjectName = "SEQ_A",
            ExecutableType = "STOCK:SEQUENCE",
            // EnumerateContainers only walks a container that has at least one child -- an empty
            // Sequence isn't a real container to it, so this needs one to be recognized at all.
            Children = [SqlTask("SQL_Inner")],
            EventHandlers = [Handler("OnError", disabled: false, SqlTask("SQL_LogOnError"))],
        };
        var package = new PackageSpec
        {
            ObjectName = "PkgWithHandlers",
            SourceDtsxPath = "in-memory.dtsx",
            Sha256 = new string('0', 64),
            FileSizeBytes = 0,
            LastWriteTimeUtc = DateTime.UnixEpoch,
            ProtectionLevelRaw = 0,
            ProtectionLevelName = "DontSaveSensitive",
            Coverage = new CoverageStats { TotalElements = 0, UnmappedElements = 0, ExcludedElements = 0, CoveragePercent = 100 },
            Executables = [seq],
        };

        var plan = PackagePlanner.Plan(package);

        var gap = Assert.Single(plan.Gaps, g => g.Location.Contains("EventHandlers[OnError]", StringComparison.Ordinal));
        Assert.True(gap.IsBlocking);
        Assert.Empty(plan.FailureHandlers);
    }
}
