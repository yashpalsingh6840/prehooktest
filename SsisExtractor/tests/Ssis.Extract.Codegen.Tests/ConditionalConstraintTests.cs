using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Conditional precedence constraints. Five of the seven forms SSIS supports are generated, and
/// which five is a measured conclusion rather than a convenience -- a real dtexec run of
/// <c>SyntheticCondConstraint.dtsx</c> measured all seven:
///
/// <list type="bullet">
/// <item><c>ExpressionAndConstraint</c> with the constraint half at Success becomes a guard on the
///   step it gates (a real <c>if</c>/<c>else</c> in the generated <c>Program.cs</c>).</item>
/// <item>A pure <c>Value=Failure</c> constraint becomes a FAILURE HANDLER -- run after the package
///   transaction is rolled back, committing on its own, with the package still failing.</item>
/// <item><c>Value=Completion</c>, <c>EvalOp=Expression</c>, and <c>EvalOp=ExpressionOrConstraint</c>
///   (the last scoped to its one evidenced Value, Failure) are measured to fire on BOTH paths --
///   each becomes the SAME task in two positions at once: an ordinary (possibly guarded) step for
///   the success path, PLUS a failure-handler counterpart for the failure path, coordinated by a
///   runtime "reached" flag so a task that already ran in-line is never re-run from the catch
///   block on some LATER, unrelated failure.</item>
/// </list>
///
/// Everything else (an ExpressionOrConstraint paired with a Value other than Failure, e.g.) stays a
/// gap -- not guessed at, since only the one shape above was ever measured against real SSIS.
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
            "(packageVariables.GetRequired<int>(\"User::Threshold\") == 0)",
            gated["SQL_GateTrue"].Guard!.CSharpPredicate);
        Assert.Equal("@[User::Threshold] == 0", gated["SQL_GateTrue"].Guard!.SsisExpression);
        Assert.Equal(
            "(packageVariables.GetRequired<int>(\"User::RowsLoaded\") > 0)",
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
    public void Generate_EmitsAnIfElseGuard_SharingOneSeededPackageVariables()
    {
        var package = LoadSyntheticFixture("SyntheticCondGuard.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null, emitSeams: true);

        var classFile = Assert.Single(result.Files, f => f.RelativePath.EndsWith("SyntheticCondGuard.cs"));

        // ONE PackageVariables (a class field), seeded with the .dtsx's own declared defaults.
        // Seeding is not cosmetic: without it a guard reading a variable nothing has assigned yet
        // would silently get Get<T>'s fallback instead of the declared value.
        Assert.Contains("private readonly PackageVariables packageVariables = new();", classFile.Content);
        Assert.Equal(1, classFile.Content.Split("PackageVariables packageVariables").Length - 1);
        Assert.Contains("packageVariables.Set(\"User::Threshold\", 0);", classFile.Content);
        Assert.Contains("packageVariables.Set(\"User::RowsLoaded\", 0);", classFile.Content);

        // Each step gets its own method, constructed unconditionally inside it (it has no side
        // effects of its own); the CONDITION is applied at the CALL SITE in RunAsync, as a real
        // if/else -- ConditionalStep no longer exists. SQL_GateTrue/SQL_GateFalse/SQL_GateFromScript
        // are precedence-independent siblings, so the emitter rewrite's phase 7 now runs the three
        // of them concurrently (see PackageStep.Wave) -- the guard's own if/else is emitted inside
        // that branch's RunBranchAsync lambda, referencing `branchUow`, not the ambient `uow`.
        Assert.Contains("internal async Task SQL_GateTrue(IUnitOfWork uow, CancellationToken ct)", classFile.Content);
        Assert.Contains("var step = new ExecuteSqlStep(", classFile.Content);
        Assert.Contains("// Guard: @[User::Threshold] == 0", classFile.Content);
        Assert.Contains("if ((packageVariables.GetRequired<int>(\"User::Threshold\") == 0))", classFile.Content);
        Assert.Contains("await SQL_GateTrue(branchUow, ct);", classFile.Content);
        Assert.Contains(
            "Log<SyntheticCondGuardPackage>().LogInformation(\"{Step}: skipped -- its precedence constraint's condition ({Condition}) evaluated false\", \"SQL_GateTrue\", \"@[User::Threshold] == 0\");",
            classFile.Content);
        Assert.Contains("_results.Add(new StepResult(\"SQL_GateTrue\", 0, 0, TimeSpan.Zero) { Skipped = true });", classFile.Content);
        Assert.Contains("await Task.WhenAll(", classFile.Content);
        Assert.Contains("private async Task RunBranchAsync(Func<IUnitOfWork, Task> body, CancellationToken ct)", classFile.Content);

        // The unconditional steps (the Script Task and the flow) still run unguarded, sequentially,
        // in the ambient transaction ahead of the concurrent wave -- a guard applied to everything
        // would pass this suite's positive assertions while breaking every package that has no
        // conditional edge.
        Assert.Contains("await SCR_SetRows(uow, ct);", classFile.Content);
        Assert.Contains("await DFT_Load(uow, ct);", classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);
    }

    [Fact]
    public void Plan_AppliesAGuard_OnAPreFlowSqlTask_NowThatNothingIsHoisted()
    {
        // SCR_First -> SQL_Truncate -> DFT_Load, with the first edge made conditional. Before the
        // emitter rewrite (phase 2), SQL_Truncate being pre-flow meant it was hoisted into a flat
        // list with nowhere to carry a condition, so the guard was reported as UNAPPLIED --
        // silently dropping it there would have generated a TRUNCATE that always runs. Now
        // SQL_Truncate is an ordinary SqlStep at its true position, so it CAN carry the guard, and
        // does: this is a genuine behavioural improvement, not just a renamed assertion.
        var package = LoadSyntheticFixture("SyntheticCondGuardUnapplied.dtsx");

        var plan = PackagePlanner.Plan(package, emitSeams: true);

        Assert.DoesNotContain(plan.Gaps, g => g.Location.EndsWith(".Constraint"));
        Assert.Empty(plan.PreLoadStatements);

        var truncateStep = Assert.Single(plan.Steps.OfType<SqlStep>(), s => s.TaskName == "SQL_Truncate");
        Assert.NotNull(truncateStep.Guard);
    }

    /// <summary>
    /// Every remaining unsupported combination -- one none of the five generated forms covers --
    /// stays a gap, and its reason names why. Raw values come from the real GAC enums (read by
    /// reflection, not guessed): they use different numbering bases, so "3" means
    /// <c>DTSExecResult.Canceled</c> on Value but <c>DTSPrecedenceEvalOp.ExpressionAndConstraint</c>
    /// on EvalOp.
    /// </summary>
    [Theory]
    [InlineData("2", "4", "OR its expression is true")] // Completion OR expr -- only Failure OR expr is evidenced
    [InlineData(null, "4", "OR its expression is true")] // Success (absent) OR expr -- same reason
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
        Assert.DoesNotContain(plan.PreLoadStatements, s => s.Sql.Contains("handled"));

        // ...while the genuinely unconditional post-flow task still is one.
        Assert.Contains(plan.Steps.OfType<SqlStep>(), s => s.TaskName == "SQL_Body");
        Assert.DoesNotContain(plan.Gaps, g => g.Location.EndsWith(".Constraint"));
    }

    [Fact]
    public void Generate_EmitsTheHandlerAsAFailureHandlerAction_NotAsAStep()
    {
        var package = LoadSyntheticFixture("SyntheticFailureHandler.dtsx");

        // includeNotifications: true -- this test specifically asserts the emitted
        // FailureHandlersRun-carrying NotifyAsync call, which only exists when notifications
        // are wired (see PackageGenerator.Generate's own includeNotifications parameter).
        var result = PackageGenerator.Generate(package, namespacePrefix: null, includeNotifications: true);

        var classFile = Assert.Single(result.Files, f => f.RelativePath.EndsWith("SyntheticFailureHandler.cs"));
        // Run strictly after the rollback, inside the catch block's own try/catch-per-handler loop
        // -- IUnitOfWork.ExecuteSqlWithoutTransactionAsync itself throws while a transaction is
        // still active, so this can never enlist in the (already-discarded) load transaction.
        Assert.Contains(
            "try { await uow.ExecuteSqlWithoutTransactionAsync(\"INSERT dbo.SyntheticFailureHandlerLog (Marker) VALUES (N'handled');\", CancellationToken.None); handlersRun.Add(\"SQL_Handler\"); }",
            classFile.Content);
        Assert.Contains("await DFT_Load(uow, ct);", classFile.Content);
        Assert.Contains("await SQL_Body(uow, ct);", classFile.Content);
        Assert.Contains("FailureHandlersRun = handlersRun", classFile.Content);
        Assert.DoesNotContain("internal async Task SQL_Handler(", classFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(classFile.Content);
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

    // ---- Dual-position forms: Completion / Expression-only / Failure-OR-expression ----------
    //
    // These three go straight through ResolveConditionalConstraints (internal, InternalsVisibleTo)
    // rather than the full PackagePlanner.Plan/WalkContainer -- the pre-load-vs-post-flow hoisting
    // walk is a separate, pre-existing concern (a step with no Data Flow Task anywhere before it in
    // its own container is hoisted into the flat pre-load list, which cannot carry a guard) that a
    // minimal two-SQL-task synthetic package would trip regardless of this round's own change. The
    // dual-position -> real-step wiring is proven for real by Generate_EmitsAReachedFlag below and
    // by the existing real-fixture-based Failure-handler tests already in this file.

    [Fact]
    public void ResolveConditionalConstraints_ResolvesACompletionConstraint_IntoAnUnguardedFailureHandler()
    {
        var gaps = new List<GenerationGap>();
        var resolved = PackagePlanner.ResolveConditionalConstraints(
            DualPositionPackage(value: "2", evalOp: null, expression: null), gaps);

        Assert.Empty(gaps);
        // Reaching the step's own line in program order already means the predecessor succeeded --
        // no explicit guard is needed for the unconditional success half.
        Assert.False(resolved.Guards.ContainsKey(@"Package\SQL_Handler"));

        var handler = Assert.Single(resolved.FailureHandlers.Values);
        Assert.Equal("SQL_Handler", handler.TaskName);
        Assert.True(handler.IsDualPosition);
        Assert.Null(handler.Guard); // unconditional on the failure path too
    }

    [Fact]
    public void ResolveConditionalConstraints_ResolvesAnExpressionOnlyConstraint_IntoTheSameGuardBothSides()
    {
        var gaps = new List<GenerationGap>();
        var resolved = PackagePlanner.ResolveConditionalConstraints(
            DualPositionPackage(value: null, evalOp: "1", expression: "@[User::Rows] > 0"), gaps);

        Assert.Empty(gaps);
        var guard = resolved.Guards[@"Package\SQL_Handler"];
        Assert.Equal("(packageVariables.GetRequired<int>(\"User::Rows\") > 0)", guard.CSharpPredicate);

        var handler = Assert.Single(resolved.FailureHandlers.Values);
        Assert.True(handler.IsDualPosition);
        // The outcome constraint is measured to be ignored entirely -- the exact same expression
        // re-gates the failure-path copy.
        Assert.Equal(guard.CSharpPredicate, handler.Guard!.CSharpPredicate);
    }

    [Fact]
    public void ResolveConditionalConstraints_ResolvesAFailureOrExpressionConstraint_IntoAnUnconditionalHandler()
    {
        var gaps = new List<GenerationGap>();
        var resolved = PackagePlanner.ResolveConditionalConstraints(
            DualPositionPackage(value: "1", evalOp: "4", expression: "@[User::Rows] > 0"), gaps);

        Assert.Empty(gaps);
        var guard = resolved.Guards[@"Package\SQL_Handler"];
        Assert.Equal("(packageVariables.GetRequired<int>(\"User::Rows\") > 0)", guard.CSharpPredicate);

        var handler = Assert.Single(resolved.FailureHandlers.Values);
        Assert.True(handler.IsDualPosition);
        // The OR's Failure half is unconditionally true once the predecessor has actually failed --
        // no re-check of the expression on the failure path.
        Assert.Null(handler.Guard);
    }

    [Fact]
    public void Generate_EmitsAReachedFlag_SoADualPositionHandlerNeverRunsTwice()
    {
        var gaps = new List<GenerationGap>();
        var resolved = PackagePlanner.ResolveConditionalConstraints(
            DualPositionPackage(value: "2", evalOp: null, expression: null), gaps);

        var request = new ProgramRequest(
            PackageName: "P",
            RootNamespace: "P",
            DbContextTypeName: "PDbContext",
            PreLoadStatements: [],
            PreLoadFileActions: [],
            Steps:
            [
                // A flow is required for Emit to proceed at all; SQL_Handler is the actual subject.
                // Explicit, DISTINCT Wave values -- both default to 0 otherwise, which the emitter
                // rewrite's phase 7 would read as "these two ran concurrently under SSIS" and wrap
                // in a Task.WhenAll, not the plain sequential shape this test actually wants to
                // assert (the real planner never produces two root steps sharing a Wave unless
                // they genuinely have no precedence constraint between them).
                new ProgramFlowStep(new ProgramFlowSpec("DFT_Load", new CsvFlowSource("FF_SRC_Load", "Load"), "LoadCsvRow", "Load", "LoadTransform", new SqlFlowSink("OLEDST_Load"))) { Wave = 0 },
                new ProgramSqlStep("SQL_Handler", "SQL_HandlerStatement") { Wave = 1 },
            ])
        {
            FailureHandlers = resolved.FailureHandlers.Values.ToList(),
        };

        var result = PackageClassEmitter.Emit(request, out _);
        var file = Assert.Single(result.Files);

        Assert.Contains("private bool SQL_HandlerReached;", file.Content);
        Assert.Contains("SQL_HandlerReached = true;", file.Content);
        Assert.Contains("await SQL_Handler(uow, ct);", file.Content);
        Assert.Contains("if (!SQL_HandlerReached)", file.Content);
        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    /// <summary>Single incoming constraint, terminal, a real Execute SQL Task -- the shape
    /// ResolveFailureHandler needs regardless of which dual-position form is under test.</summary>
    private static PackageSpec DualPositionPackage(string? value, string? evalOp, string? expression)
    {
        ExecutableSpec Sql(string name, string sql) => new()
        {
            RefId = $@"Package\{name}",
            ExecutableType = "Microsoft.ExecuteSQLTask",
            ObjectName = name,
            ExecuteSqlTask = new ExecuteSqlTaskPayload { SqlStatementSource = sql },
        };

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
            Executables =
            [
                Sql("SQL_Body", "SELECT 1;"),
                Sql("SQL_Handler", "INSERT dbo.L (M) VALUES (N'handled');"),
            ],
            PrecedenceConstraints =
            [
                new() { From = @"Package\SQL_Body", To = @"Package\SQL_Handler", Value = value, EvalOp = evalOp, Expression = expression },
            ],
            Variables =
            [
                new VariableSpec
                {
                    Namespace = "User", ObjectName = "Rows", DeclaredDataTypeName = "Int32", Value = "0",
                    OwningContainerRefId = "Package",
                },
            ],
        };
    }
}
