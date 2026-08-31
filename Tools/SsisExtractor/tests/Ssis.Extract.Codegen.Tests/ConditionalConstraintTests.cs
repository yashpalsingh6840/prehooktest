using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Conditional precedence constraints. Exactly one of the seven forms SSIS supports is generated --
/// <c>DTSPrecedenceEvalOp.ExpressionAndConstraint</c> with the constraint half at Success -- and
/// that choice is a measured conclusion, not a convenience: a real dtexec run of
/// <c>SyntheticCondConstraint.dtsx</c> showed every other form running its successor on the FAILURE
/// path, which a single whole-package transaction cannot express at all.
///
/// The negative cases matter as much as the positive one here. A guard that silently fails to
/// apply, or a form charitably read as unconditional, would run work SSIS would have skipped while
/// reporting nothing -- the same failure class as the disabled-executable and
/// Script-Component-passthrough bugs.
/// </summary>
public class ConditionalConstraintTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    [Fact]
    public void Plan_TranslatesASuccessAndExpressionConstraint_IntoAGuardOnTheStepItGates()
    {
        var package = LoadSyntheticFixture("SyntheticCondGuard.dtsx");

        var plan = PackagePlanner.Plan(package, emitSeams: true);

        var gated = plan.Steps.OfType<SqlStep>().ToDictionary(s => s.TaskName);
        Assert.Equal(
            "(v.Get<int>(\"User::Threshold\") == 0)",
            gated["SQL_GateTrue"].Guard!.CSharpPredicate);
        Assert.Equal("@[User::Threshold] == 0", gated["SQL_GateTrue"].Guard!.SsisExpression);
        Assert.Equal(
            "(v.Get<int>(\"User::RowsLoaded\") > 0)",
            gated["SQL_GateFromScript"].Guard!.CSharpPredicate);

        // The unconditional steps stay unguarded -- a guard applied to everything would pass this
        // suite's positive assertions while breaking every package that has no conditional edge.
        Assert.Null(Assert.Single(plan.Steps.OfType<FlowStep>()).Guard);
        Assert.Null(Assert.Single(plan.Steps.OfType<ScriptTaskStep>()).Guard);

        // Design-time defaults, deduplicated across the three gates and ordinal-sorted.
        Assert.Collection(plan.VariableSeeds,
            seed => Assert.Equal(("User::RowsLoaded", "int", "0"), (seed.SsisName, seed.ClrTypeName, seed.CSharpLiteral)),
            seed => Assert.Equal(("User::Threshold", "int", "0"), (seed.SsisName, seed.ClrTypeName, seed.CSharpLiteral)));

        Assert.DoesNotContain(plan.Gaps, g => g.Location.EndsWith(".Constraint"));
    }

    [Fact]
    public void Generate_WrapsAGatedStepInAConditionalStep_SharingOneSeededPackageVariables()
    {
        var package = LoadSyntheticFixture("SyntheticCondGuard.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null, emitSeams: true);

        var program = Assert.Single(result.Files, f => f.RelativePath.EndsWith("Program.cs"));

        // ONE PackageVariables, seeded with the .dtsx's own declared defaults. Seeding is not
        // cosmetic: without it a guard reading a variable nothing has assigned yet would silently
        // get Get<T>'s fallback instead of the declared value.
        Assert.Equal(1, program.Content.Split("new PackageVariables()").Length - 1);
        Assert.Contains("packageVariables.Set(\"User::Threshold\", 0);", program.Content);
        Assert.Contains("packageVariables.Set(\"User::RowsLoaded\", 0);", program.Content);

        Assert.Contains("var sQL_GateTrueStepGated = new ConditionalStep(", program.Content);
        Assert.Contains("        sQL_GateTrueStep,", program.Content);
        Assert.Contains("        v => (v.Get<int>(\"User::Threshold\") == 0),", program.Content);
        Assert.Contains("        sp.GetRequiredService<ILogger<ConditionalStep>>());", program.Content);

        // The GATED local is what reaches Steps, not the bare one -- getting this wrong would
        // compile cleanly and run the step unconditionally, so it is asserted directly.
        Assert.Contains("sQL_GateTrueStepGated, sQL_GateFalseStepGated, sQL_GateFromScriptStepGated]", program.Content);
        CodeAssertions.AssertNoSyntaxErrors(program.Content);
    }

    [Fact]
    public void Plan_ReportsAGuardThatNeverReachesAStep_RatherThanRunningTheGatedWorkAnyway()
    {
        // SCR_First -> SQL_Truncate -> DFT_Load, with the first edge made conditional.
        // SQL_Truncate is pre-flow, so it is hoisted into the flat PreLoadStatements list, which
        // has nowhere to carry a condition. Silently dropping the guard there would generate a
        // TRUNCATE that always runs.
        var package = LoadSyntheticFixture("SyntheticCondGuardUnapplied.dtsx");

        var plan = PackagePlanner.Plan(package, emitSeams: true);

        var gap = Assert.Single(plan.Gaps, g => g.Location.EndsWith(".Constraint"));
        Assert.Equal("SyntheticCondGuardUnapplied.SQL_Truncate.Constraint", gap.Location);
        Assert.Contains("does not become a step the condition can be attached to", gap.Reason);
        Assert.Contains("hoisted into the flat pre-load list", gap.Reason);
        Assert.True(gap.IsBlocking);
        Assert.Equal(GapKind.ConditionalConstraint, gap.Kind);
        Assert.Contains(plan.PreLoadStatements, s => s.Contains("TRUNCATE"));
    }

    /// <summary>
    /// Every form other than "Success AND expression" stays a gap, and its reason names the MEASURED
    /// behaviour. Raw values come from the real GAC enums (read by reflection, not guessed): they use
    /// different numbering bases, so "3" means <c>DTSExecResult.Canceled</c> on Value but
    /// <c>DTSPrecedenceEvalOp.ExpressionAndConstraint</c> on EvalOp.
    /// </summary>
    [Theory]
    [InlineData("1", null, "only when the preceding executable FAILS")]
    [InlineData("2", null, "on completion, pass or fail")]
    [InlineData(null, "1", "the outcome constraint being IGNORED entirely")]
    [InlineData(null, "4", "OR its expression is true")]
    [InlineData("1", "3", "conditionally on DTS:Value=1/DTS:EvalOp=3")]
    public void Plan_ReportsAnUnsupportedConstraintForm_WithItsMeasuredReason(
        string? value, string? evalOp, string expectedFragment)
    {
        var plan = PackagePlanner.Plan(PackageWithOneConstraint(value, evalOp, "@[User::Rows] > 0"));

        var gap = Assert.Single(plan.Gaps, g => g.Location.EndsWith(".Constraint"));
        Assert.Equal("P.A-B.Constraint", gap.Location);
        Assert.Contains(expectedFragment, gap.Reason);
        Assert.Contains("Measured against real SSIS", gap.Reason);
        Assert.True(gap.IsBlocking);
        Assert.Equal(GapKind.ConditionalConstraint, gap.Kind);
    }

    [Fact]
    public void Plan_StaysSilent_ForTheUnconditionalDefaults()
    {
        // Value absent or "0" (DTSExecResult.Success) and EvalOp absent or "2"
        // (DTSPrecedenceEvalOp.Constraint) are the schema defaults -- the ordinary on-success edge
        // every package is full of. Reporting those would attach a permanent gap to every package
        // in a portfolio, which is the noise that trains people to ignore the gap list.
        foreach (var (value, evalOp) in new (string?, string?)[] { (null, null), ("0", null), (null, "2"), ("0", "2") })
        {
            var plan = PackagePlanner.Plan(PackageWithOneConstraint(value, evalOp, null));
            Assert.DoesNotContain(plan.Gaps, g => g.Location.EndsWith(".Constraint"));
        }
    }

    [Fact]
    public void Plan_ReportsAGap_WhenASupportedFormsExpressionCannotBeTranslated()
    {
        // The form is right but the expression is not translatable -- here because no variable of
        // that name is declared. Reported, never charitably read as "always true".
        var plan = PackagePlanner.Plan(PackageWithOneConstraint(null, "3", "@[User::NotDeclared] > 0"));

        var gap = Assert.Single(plan.Gaps, g => g.Location.EndsWith(".Constraint"));
        Assert.Contains("succeeds AND its expression is true", gap.Reason);
        Assert.Equal(GapKind.ConditionalConstraint, gap.Kind);
    }

    private static PackageSpec PackageWithOneConstraint(string? value, string? evalOp, string? expression) =>
        new()
        {
            ObjectName = "P",
            SourceDtsxPath = "P.dtsx",
            Sha256 = "",
            FileSizeBytes = 0,
            LastWriteTimeUtc = default,
            ProtectionLevelRaw = 0,
            ProtectionLevelName = "DontSaveSensitive",
            Coverage = new CoverageStats
            {
                TotalElements = 0, UnmappedElements = 0, ExcludedElements = 0, CoveragePercent = 100,
            },
            PrecedenceConstraints =
            [
                new PrecedenceConstraintSpec
                {
                    From = @"Package\A", To = @"Package\B",
                    Value = value, EvalOp = evalOp, Expression = expression,
                },
            ],
        };
}
