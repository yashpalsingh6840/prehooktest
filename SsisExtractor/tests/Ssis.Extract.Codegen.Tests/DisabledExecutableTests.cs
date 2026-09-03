using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// A disabled executable must generate nothing. Before this was fixed, DTS:Disabled was extracted
/// and reported as a finding by RulesEngine but read NOWHERE in Ssis.Extract.Codegen, so a task
/// the package author had switched off was generated as though it were live -- shipped output for
/// RBC_Demo_ETL's own Package_Legacy really did carry SQL_LegacyStep_DISABLED's INSERT.
///
/// Unlike the parallelism advisory, this is a correctness bug rather than a performance
/// difference: the generated job wrote a row SSIS never wrote.
///
/// What "skip" has to mean was MEASURED against the real SSIS runtime, not reasoned about --
/// SyntheticDisabledTask.dtsx run under dtexec (see that fixture's own doc comment and
/// synthetic-disabled-task-tables.sql). A disabled task's SUCCESSORS still run, so skipping
/// exactly the disabled node and nothing else is the faithful translation.
/// </summary>
public class DisabledExecutableTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    [Fact]
    public void Plan_SkipsADisabledTask_ButStillPlansEverythingOrderedAfterIt()
    {
        // SQL_MarkPre -> SQL_Disabled_MarkNever -> DFT_Load -> SQL_MarkPost -> SEQ_Disabled
        var package = LoadSyntheticFixture("SyntheticDisabledTask.dtsx");

        var plan = PackagePlanner.Plan(package);

        // The disabled task's own statement appears nowhere -- neither position it could have
        // taken (pre-load list, or a post-flow SqlStep).
        Assert.DoesNotContain(plan.PreLoadStatements, s => s.Contains("disabled-ran"));
        Assert.DoesNotContain(plan.Steps.OfType<SqlStep>(), s => s.Sql.Contains("disabled-ran"));

        // Its successors are unaffected, which is the half that a naive "skip the branch" fix
        // would have got wrong: the flow still plans, and the Execute SQL Task after the flow is
        // still correctly a post-flow step rather than a pre-load statement.
        Assert.Single(plan.Flows);
        Assert.Contains(plan.PreLoadStatements, s => s.Contains("'pre'"));
        Assert.Contains(plan.Steps.OfType<SqlStep>(), s => s.TaskName == "SQL_MarkPost");
    }

    [Fact]
    public void Plan_SkipsADisabledContainer_WithoutDescendingIntoIt()
    {
        // SEQ_Disabled is disabled but holds one ENABLED Execute SQL Task. SSIS runs neither
        // (measured), so the walk must not descend at all -- a container-level check, not a
        // per-child one.
        var package = LoadSyntheticFixture("SyntheticDisabledTask.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.DoesNotContain(plan.PreLoadStatements, s => s.Contains("inside-disabled-seq"));
        Assert.DoesNotContain(plan.Steps.OfType<SqlStep>(), s => s.Sql.Contains("inside-disabled-seq"));
        Assert.DoesNotContain(plan.Steps.OfType<SqlStep>(), s => s.TaskName == "SQL_InsideDisabledSeq");
    }

    [Fact]
    public void Plan_ReportsEverySkip_AsANonBlockingAdvisoryWithNoWorkPacket()
    {
        // Skipping is CORRECT, so this must not block generation -- but it must still be said out
        // loud, or a reader diffing the .dtsx against the code finds a task with no counterpart
        // and cannot tell deliberate handling from a bug. And there is nothing for an AI or a
        // human to fill in, so it must not produce a work packet either.
        var package = LoadSyntheticFixture("SyntheticDisabledTask.dtsx");

        var plan = PackagePlanner.Plan(package);
        var skips = plan.Gaps.Where(g => g.Location.EndsWith(".Disabled")).ToList();

        Assert.Equal(2, skips.Count);
        Assert.Contains(skips, g => g.Location == "SQL_Disabled_MarkNever.Disabled");
        Assert.Contains(skips, g => g.Location == "SEQ_Disabled.Disabled");

        foreach (var skip in skips)
        {
            Assert.False(skip.IsBlocking);
            Assert.Equal(GapKind.Unclassified, skip.Kind);
            Assert.Equal(GapTier.Advisory, GapIdentity.TierOf(skip));
            Assert.False(GapIdentity.HasWorkPacket(skip));
        }

        // The container's advisory says how much went with it, so "one task skipped" is never
        // confused with "a whole branch skipped".
        Assert.Contains("its 1 child executable(s)",
            Assert.Single(skips, g => g.Location == "SEQ_Disabled.Disabled").Reason);
    }

    [Fact]
    public void Plan_DoesNotCountADisabledSibling_AsParallelism()
    {
        // A wave of {enabled, disabled} is not concurrency, and calling it concurrency was a live
        // false positive on a real package: Package_Legacy's SQL_AtomicSwap fans out to
        // DFT_FixedWidthImport and to SQL_LegacyStep_DISABLED, so SSIS never ran two things at
        // once there, yet the advisory claimed it did.
        //
        // This fixture is SyntheticFlatFileDestination.dtsx with DTS:Disabled="True" added to one
        // of its three precedence-independent root flows (a byte-preserving derivation -- see
        // trap 18), so the advisory must drop from 3 to 2 rather than staying at 3.
        var all = PackagePlanner.Plan(LoadSyntheticFixture("SyntheticFlatFileDestination.dtsx"));
        var oneDisabled = PackagePlanner.Plan(LoadSyntheticFixture("SyntheticFlatFileDestinationDisabled.dtsx"));

        Assert.Contains("up to 3 executables",
            Assert.Single(all.Gaps, g => g.Location.EndsWith(".Parallelism")).Reason);
        Assert.Contains("up to 2 executables",
            Assert.Single(oneDisabled.Gaps, g => g.Location.EndsWith(".Parallelism")).Reason);

        // ...and the disabled flow itself is gone from the plan, not merely uncounted.
        Assert.Equal(3, all.Flows.Count);
        Assert.Equal(2, oneDisabled.Flows.Count);
    }
}
