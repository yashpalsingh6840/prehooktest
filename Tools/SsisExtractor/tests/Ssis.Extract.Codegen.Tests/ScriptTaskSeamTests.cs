using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// The Script Task half of Tier 2 -- the counterpart to the Script Component column seams. A
/// Script Task is a whole <c>ILoadTask</c> rather than one method on a transform, so it needs its
/// own emitter, its own position in the step order, and (uniquely) a way for two ported tasks to
/// share a variable.
///
/// Without <c>--seams</c> a Script Task keeps the plain "unsupported executable type" gap and is
/// omitted, exactly as before -- asserted below, because that is what keeps every existing
/// pipeline's output unchanged.
/// </summary>
public class ScriptTaskSeamTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    [Fact]
    public void Plan_ProducesAScriptTaskStep_AtItsTruePositionRelativeToTheFlow()
    {
        // SQL_Truncate -> SCR_Start -> DFT_Load -> SCR_Finish. A Script Task always becomes a step,
        // and (emitter rewrite phase 2) so does SQL_Truncate now -- nothing is hoisted any more --
        // so the plain Steps order is the only thing preserving everyone's real position.
        var package = LoadSyntheticFixture("SyntheticScriptTaskSeams.dtsx");

        var plan = PackagePlanner.Plan(package, emitSeams: true);

        Assert.Collection(plan.Steps,
            step => Assert.Equal("SQL_Truncate", Assert.IsType<SqlStep>(step).TaskName),
            step => Assert.Equal("SCR_Start", Assert.IsType<ScriptTaskStep>(step).Task.ObjectName),
            step => Assert.IsType<FlowStep>(step),
            step => Assert.Equal("SCR_Finish", Assert.IsType<ScriptTaskStep>(step).Task.ObjectName));

        // The Execute SQL Task is genuinely pre-flow and ordered first -- emitter rewrite phase 2:
        // it's an ordinary SqlStep like everything else now, nothing is hoisted, so there is no
        // ordering hazard (and no ".Hoisting" gap kind) left to report at all.
        Assert.Contains(plan.Steps.OfType<SqlStep>(), s => s.Sql.Contains("TRUNCATE"));
        Assert.DoesNotContain(plan.Gaps, g => g.Location.EndsWith(".Hoisting"));
    }

    [Fact]
    public void Plan_KeepsThePlainUnsupportedGap_WhenSeamsAreDisabled()
    {
        var package = LoadSyntheticFixture("SyntheticScriptTaskSeams.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.DoesNotContain(plan.Steps, step => step is ScriptTaskStep);
        var gap = Assert.Single(plan.Gaps, g => g.Location == "SCR_Start");
        Assert.Contains("not supported by this planner", gap.Reason);
        Assert.Equal(GapKind.ScriptTask, gap.Kind);
    }

    [Fact]
    public void Generate_EmitsOneSeamClassPerScriptTask_SharingASinglePackageVariables()
    {
        var package = LoadSyntheticFixture("SyntheticScriptTaskSeams.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null, emitSeams: true);

        var start = Assert.Single(result.Files, f => f.RelativePath.EndsWith("ScriptTasks/SCR_StartScriptTask.cs"));
        Assert.Contains("public sealed partial class SCR_StartScriptTask(PackageVariables variables, IServiceProvider services) : ILoadTask", start.Content);
        Assert.Contains("private partial Task RunScriptAsync(ScriptTaskContext ctx, CancellationToken ct);", start.Content);
        Assert.Contains("public string Name => \"SCR_Start\";", start.Content);
        // The declared variable surface is carried into the file so a porter can see it without
        // opening the packet.
        Assert.Contains("Declared ReadWriteVariables: User::Marker", start.Content);
        CodeAssertions.AssertNoSyntaxErrors(start.Content);

        // ONE PackageVariables instance (a class field), shared by BOTH tasks -- that sharing is
        // the whole reason the type exists, so it is asserted rather than assumed.
        var classFile = Assert.Single(result.Files, f => f.RelativePath.EndsWith("SyntheticScriptTaskSeams.cs"));
        Assert.Contains("private readonly PackageVariables packageVariables = new();", classFile.Content);
        Assert.Equal(1, classFile.Content.Split("PackageVariables packageVariables").Length - 1);
        Assert.Contains("new SCR_StartScriptTask(packageVariables, services)", classFile.Content);
        Assert.Contains("new SCR_FinishScriptTask(packageVariables, services)", classFile.Content);
        Assert.Contains("using SyntheticScriptTaskSeams.ScriptTasks;", classFile.Content);

        // Still blocking, and still classified Tier 2 so the work packet is still produced -- a
        // seam is outstanding work, not a resolution.
        var gap = Assert.Single(result.Gaps, g => g.Location == "SCR_Start.ScriptTask" && g.Kind == GapKind.ScriptTask);
        Assert.True(gap.IsBlocking);
        Assert.Contains("CS8795", gap.Reason);

        // Companion "Script Task / Script Component seam" taxonomy row: a filled seam is human
        // logic, and a test for it is a separate, non-blocking TEST-ORACLE work item, sharing the
        // same Location so both land under this task in gaps.json.
        var testGap = Assert.Single(result.Gaps, g => g.Location == "SCR_Start.ScriptTask" && g.Kind == GapKind.TestOracle);
        Assert.False(testGap.IsBlocking);
    }

    [Fact]
    public void Plan_OrdersAPreFlowSqlTaskAfterAPrecedingScriptTask_WithNoHoistingHazard()
    {
        // SCR_First -> SQL_Truncate -> DFT_Load. Before the emitter rewrite (phase 2), SQL_Truncate
        // being pre-flow meant it was HOISTED ahead of every step -- including SCR_First, silently
        // inverting the package's own real order, reported as a blocking ".Hoisting" gap. Now
        // nothing is hoisted: SQL_Truncate is an ordinary SqlStep at its true topological position,
        // so the hazard (and the gap kind that existed only to catch it) is gone entirely.
        var package = LoadSyntheticFixture("SyntheticScriptTaskHoistInversion.dtsx");

        var plan = PackagePlanner.Plan(package, emitSeams: true);

        Assert.DoesNotContain(plan.Gaps, g => g.Location.EndsWith(".Hoisting"));
        Assert.Empty(plan.PreLoadStatements);

        Assert.Collection(plan.Steps,
            step => Assert.Equal("SCR_First", Assert.IsType<ScriptTaskStep>(step).Task.ObjectName),
            step => Assert.Equal("SQL_Truncate", Assert.IsType<SqlStep>(step).TaskName),
            step => Assert.IsType<FlowStep>(step));
    }
    // Plan_ReportsAConditionalPrecedenceConstraint_... used to live here, because conditional
    // precedence constraints were DISCOVERED while building Script Task seams. They now have their
    // own feature and their own file: ConditionalConstraintTests covers all five unsupported forms
    // (this covered two), the unconditional defaults, the translated form, and the guard that never
    // reaches a step -- so keeping a narrower copy here would only be a second thing to update.
}
