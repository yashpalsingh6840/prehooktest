using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Conditional precedence constraints. Two of the seven forms SSIS supports are generated, and which
/// two is a measured conclusion rather than a convenience -- a real dtexec run of
/// <c>SyntheticCondConstraint.dtsx</c> measured all seven:
///
/// <list type="bullet">
/// <item><c>ExpressionAndConstraint</c> with the constraint half at Success becomes a guard on the
///   step it gates (an <c>Etl.Core</c> ConditionalStep).</item>
/// <item>A pure <c>Value=Failure</c> constraint becomes a FAILURE HANDLER -- run after the package
///   transaction is rolled back, committing on its own, with the package still failing.</item>
/// </list>
///
/// The remaining forms all fire on BOTH the success and the failure path, which would need the same
/// task in two positions at once; they stay gaps.
///
/// The negative cases matter as much as the positive ones here. A guard that silently fails to
/// apply, a form charitably read as unconditional, or a handler that ALSO stays an ordinary step,
/// would each run work SSIS would have skipped while reporting nothing -- the same failure class as the disabled-executable and
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
            "(v.GetRequired<int>(\"User::Threshold\") == 0)",
            gated["SQL_GateTrue"].Guard!.CSharpPredicate);
        Assert.Equal("@[User::Threshold] == 0", gated["SQL_GateTrue"].Guard!.SsisExpression);
        Assert.Equal(
            "(v.GetRequired<int>(\"User::RowsLoaded\") > 0)",
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
        Assert.Contains("        v => (v.GetRequired<int>(\"User::Threshold\") == 0),", program.Content);
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

    // ---- Failure precedence constraints (DTS:Value="1") ------------------------------------

    [Fact]
    public void Plan_ResolvesAFailureConstraint_IntoAHandlerAndExcludesItFromTheSteps()
    {
        // SQL_Truncate -> DFT_Load -> SQL_Body -(Failure)-> SQL_Handler.
        var package = LoadSyntheticFixture("SyntheticFailureHandler.dtsx");

        var plan = PackagePlanner.Plan(package);

        var handler = Assert.Single(plan.FailureHandlers);
        Assert.Equal("SQL_Handler", handler.TaskName);
        Assert.Contains("VALUES (N'handled')", handler.Sql);

        // The whole point: the handler task must NOT also be an ordinary step or a pre-load
        // statement. Generating it in both places is exactly the bug this closes -- it would run on
        // every successful run.
        Assert.DoesNotContain(plan.Steps.OfType<SqlStep>(), s => s.TaskName == "SQL_Handler");
        Assert.DoesNotContain(plan.PreLoadStatements, s => s.Contains("handled"));

        // ...while the genuinely unconditional post-flow task still is one.
        Assert.Contains(plan.Steps.OfType<SqlStep>(), s => s.TaskName == "SQL_Body");
        Assert.DoesNotContain(plan.Gaps, g => g.Location.EndsWith(".Constraint"));
    }

    [Fact]
    public void Generate_EmitsTheHandlerAsAFailureHandlerAction_NotAsAStep()
    {
        var package = LoadSyntheticFixture("SyntheticFailureHandler.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var program = Assert.Single(result.Files, f => f.RelativePath.EndsWith("Program.cs"));
        Assert.Contains("FailureHandlers: [new FailureHandlerAction(\"SQL_Handler\",", program.Content);
        Assert.Contains("Steps: [syntheticFailureHandlerTargetFlow, sQL_BodyStep],", program.Content);
        Assert.DoesNotContain("sQL_HandlerStep", program.Content);
        CodeAssertions.AssertNoSyntaxErrors(program.Content);
    }

    /// <summary>
    /// The shapes a Failure constraint is NOT taken as a handler. Each would otherwise generate
    /// something wrong rather than merely incomplete: a task also reachable on the success path
    /// would be silently DROPPED from the steps, and a non-terminal one would lose its successors.
    /// </summary>
    [Theory]
    [InlineData("dataflow", "rather than an Execute SQL Task")]
    [InlineData("extra-incoming", "incoming precedence constraints")]
    [InlineData("non-terminal", "has its own successors")]
    [InlineData("disabled", "is disabled")]
    public void Plan_ReportsAGap_ForAFailureConstraintShapeItWillNotTake(string shape, string expectedFragment)
    {
        var plan = PackagePlanner.Plan(FailureConstraintPackage(shape));

        var gap = Assert.Single(plan.Gaps, g => g.Location.EndsWith(".Constraint"));
        Assert.Contains("only when the preceding executable FAILS", gap.Reason);
        Assert.Contains(expectedFragment, gap.Reason);
        Assert.True(gap.IsBlocking);
        Assert.Equal(GapKind.ConditionalConstraint, gap.Kind);
        Assert.Empty(plan.FailureHandlers);
    }

    private static PackageSpec FailureConstraintPackage(string shape)
    {
        ExecutableSpec Sql(string name, string sql, bool disabled = false) => new()
        {
            RefId = $@"Package\{name}",
            ExecutableType = "Microsoft.ExecuteSQLTask",
            ObjectName = name,
            Disabled = disabled ? true : null,
            ExecuteSqlTask = new ExecuteSqlTaskPayload { SqlStatementSource = sql },
        };

        var body = Sql("SQL_Body", "SELECT 1;");
        var handler = shape == "dataflow"
            ? new ExecutableSpec
            {
                RefId = @"Package\SQL_Handler",
                ExecutableType = "Microsoft.Pipeline",
                ObjectName = "SQL_Handler",
            }
            : Sql("SQL_Handler", "INSERT dbo.L (M) VALUES (N'handled');", disabled: shape == "disabled");

        List<PrecedenceConstraintSpec> constraints =
        [
            new() { From = @"Package\SQL_Body", To = @"Package\SQL_Handler", Value = "1" },
        ];

        if (shape == "extra-incoming")
            constraints.Add(new PrecedenceConstraintSpec { From = @"Package\SQL_Other", To = @"Package\SQL_Handler" });
        if (shape == "non-terminal")
            constraints.Add(new PrecedenceConstraintSpec { From = @"Package\SQL_Handler", To = @"Package\SQL_After" });

        return new PackageSpec
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
            Executables = [body, handler, Sql("SQL_Other", "SELECT 2;"), Sql("SQL_After", "SELECT 3;")],
            PrecedenceConstraints = constraints,
        };
    }
}
