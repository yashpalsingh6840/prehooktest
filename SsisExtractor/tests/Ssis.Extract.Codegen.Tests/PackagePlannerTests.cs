using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Codegen.Tests;

public class PackagePlannerTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    // tests/Ssis.Extract.Codegen.Tests -> Tools/SsisExtractor -> repo root -> SSIS/ -- where
    // SyntheticParallelShapes.dtsx lives (registered in SSIS.dtproj, unlike the other
    // synthetic fixtures under Ssis.Extract.Tests/Fixtures), same lookup depth
    // SyntheticParallelShapesTests.SsisProjectDir uses from its own sibling test project.
    private static readonly string SsisProjectDir = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(ThisFilePath())!, "..", "..", "..", "..", "..", "SSIS_Packages", "SSIS"));

    // Tools/SsisExtractor/tests/Ssis.Extract.Codegen.Tests -> tests/Ssis.Extract.Tests/Fixtures --
    // where the synthetic component-coverage fixtures live (unlike TestFixtures.LoadPackage,
    // which only ever points at the real PoC packages under SSIS/).
    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    [Fact]
    public void Plan_RecursesIntoASequenceContainer_ForTheSyntheticNestedContainerFixture()
    {
        var package = LoadSyntheticFixture("SyntheticNestedContainer.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        // The Execute SQL Task lives INSIDE the Sequence Container -- proves nested pre-flow SQL
        // is an ordinary step (emitter rewrite phase 2: nothing is hoisted any more), folded into
        // the same flat Steps list a root-level one would be, and carries its real container path.
        var truncateStep = Assert.Single(plan.Steps.OfType<SqlStep>(), s => s.TaskName == "SQL_Truncate");
        Assert.Equal("TRUNCATE TABLE dbo.SyntheticNestedTarget;", truncateStep.Sql);
        Assert.Equal(["SEQ_Load"], truncateStep.ContainerPath);
        Assert.Empty(plan.PreLoadStatements); // always empty as of phase 2, see PreLoadSqlStatementPlan's own doc comment

        Assert.Equal(2, plan.Flows.Count);
        // DFT_NestedLoad is nested inside SEQ_Load; DFT_RootLoad is a root-level sibling that
        // runs AFTER the whole container (a precedence constraint on the container itself) --
        // this ordering proves the container's contents are correctly interleaved with its
        // parent's own order, not just appended at the end regardless of position.
        Assert.Equal("DFT_NestedLoad", plan.Flows[0].TaskName);
        Assert.Equal("DFT_RootLoad", plan.Flows[1].TaskName);
    }

    [Fact]
    public void Plan_ReportsANamedGap_ForEachExecutePackageTaskMode_RatherThanAGenericUnsupportedMessage()
    {
        // Real fixture (object-model-built, verified against a live SSIS 16.0 round trip -- see
        // ExecutePackageTaskPayload's own doc comment). Two independent sibling tasks, one per
        // real evidenced mode -- neither should fall through to WalkContainer's generic
        // "executable type ... is not supported" message, and neither gets an AI work packet
        // (Tier 3: composing two generated packages is missing TOOL support, not something a
        // human/AI translation could close per package).
        var package = LoadSyntheticFixture("SyntheticExecutePackageTask.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Flows);
        // Plus a non-blocking Parallelism advisory (the two tasks are unordered siblings) --
        // unrelated to this test's own point, not asserted on further here.

        var fileRef = Assert.Single(plan.Gaps, g => g.Location == "EPT_FileRef");
        Assert.Equal(GapKind.Unclassified, fileRef.Kind);
        Assert.Equal(GapTier.MissingToolSupport, GapIdentity.TierOf(fileRef));
        Assert.Contains("file/legacy reference", fileRef.Reason);
        Assert.Contains("ChildPackage.dtsx", fileRef.Reason);
        Assert.Contains("CM_FILE_ChildPackage", fileRef.Reason);
        Assert.DoesNotContain("is not supported by this planner yet", fileRef.Reason);

        var projRef = Assert.Single(plan.Gaps, g => g.Location == "EPT_ProjectRef");
        Assert.Equal(GapKind.Unclassified, projRef.Kind);
        Assert.Equal(GapTier.MissingToolSupport, GapIdentity.TierOf(projRef));
        Assert.Contains("project reference", projRef.Reason);
        Assert.Contains("ChildInSameProject.dtsx", projRef.Reason);
    }

    [Fact]
    public void Plan_ReportsANamedGap_ForATransferSqlServerObjectsTask_RatherThanAGenericUnsupportedMessage()
    {
        // Hand-built ExecutableSpec, same "no dtsx/object-model build needed" reasoning as
        // UnionPackage/StandaloneSortPackage elsewhere in this file -- the exact real attribute
        // values below were read directly from a genuine SSDT-authored package
        // (D:\PoC\SSIS_Packages_From_GitHub\ETL-SSIS-Real-Scenarios\UseCase_55\...\Package.dtsx,
        // "Transfer SQL Server Objects Task"; see TransferSqlServerObjectsTaskPayload's own doc
        // comment), not invented. Documented-gap-only (Phase 6): this must land as a named,
        // specific, actionable gap -- not WalkContainer's generic "executable type ... is not
        // supported" fallback -- and must never be silently guessed at as generated code.
        var package = new PackageSpec
        {
            ObjectName = "Package",
            SourceDtsxPath = "Package.dtsx",
            Sha256 = "",
            FileSizeBytes = 0,
            LastWriteTimeUtc = default,
            ProtectionLevelRaw = 0,
            ProtectionLevelName = "DontSaveSensitive",
            Coverage = new CoverageStats { TotalElements = 0, UnmappedElements = 0, ExcludedElements = 0, CoveragePercent = 100 },
            Executables =
            [
                new ExecutableSpec
                {
                    RefId = @"Package\Transfer SQL Server Objects Task",
                    ExecutableType = "Microsoft.TransferSqlServerObjectsTask",
                    ObjectName = "Transfer SQL Server Objects Task",
                    TransferSqlServerObjectsTask = new TransferSqlServerObjectsTaskPayload
                    {
                        SourceConnectionRefRaw = "{0068FB23-8DF9-447A-AD0B-DED0CBFED5CC}",
                        SourceConnectionName = ".",
                        DestinationConnectionRefRaw = "{05F4CEB5-52A8-4CD0-894B-4B7E6D2A5099}",
                        DestinationConnectionName = @"AMR\MSSQLSERVER01",
                        SourceDatabase = "SSIS",
                        DestinationDatabase = "test",
                        TablesListRaw = "4,15,[dbo].[Country],16,[dbo].[Currency],17,[dbo].[customer1],17,[dbo].[customer2],",
                        Tables = ["[dbo].[Country]", "[dbo].[Currency]", "[dbo].[customer1]", "[dbo].[customer2]"],
                        DropObjectsFirst = true,
                        IncludeDependentObjects = true,
                        CopyData = true,
                        CopyIndexes = true,
                        CopyPrimaryKeys = true,
                        CopyForeignKeys = true,
                    },
                },
            ],
        };

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Flows);
        var gap = Assert.Single(plan.Gaps, g => g.Location == "Transfer SQL Server Objects Task");
        Assert.Equal(GapKind.Unclassified, gap.Kind);
        Assert.Equal(GapTier.MissingToolSupport, GapIdentity.TierOf(gap));
        Assert.DoesNotContain("is not supported by this planner yet", gap.Reason);

        // Names the real source/destination databases, servers, table list, and flags -- not a
        // generic "unsupported task type" message.
        Assert.Contains("[dbo].[Country]", gap.Reason);
        Assert.Contains("[dbo].[Currency]", gap.Reason);
        Assert.Contains("[dbo].[customer1]", gap.Reason);
        Assert.Contains("[dbo].[customer2]", gap.Reason);
        Assert.Contains("SSIS", gap.Reason);
        Assert.Contains("'.'", gap.Reason);
        Assert.Contains("test", gap.Reason);
        Assert.Contains(@"AMR\MSSQLSERVER01", gap.Reason);
        Assert.Contains("DropObjectsFirst", gap.Reason);
        Assert.Contains("IncludeDependentObjects", gap.Reason);
        Assert.Contains("CopyData", gap.Reason);
        Assert.Contains("CopyIndexes", gap.Reason);
        Assert.Contains("CopyPrimaryKeys", gap.Reason);
        Assert.Contains("CopyForeignKeys", gap.Reason);
        Assert.Contains("SMO Transfer", gap.Reason);
        Assert.Contains("DACPAC", gap.Reason);
    }

    [Fact]
    public void Plan_ReportsAGap_ForATransferSqlServerObjectsTask_WithNoTablesListRecorded()
    {
        // A TablesList-less transfer (whole-database transfer) is real per the SMO object model,
        // even though it isn't the one evidenced example -- must still read as an honest,
        // specific statement rather than a blank/misleading one.
        var package = new PackageSpec
        {
            ObjectName = "Package",
            SourceDtsxPath = "Package.dtsx",
            Sha256 = "",
            FileSizeBytes = 0,
            LastWriteTimeUtc = default,
            ProtectionLevelRaw = 0,
            ProtectionLevelName = "DontSaveSensitive",
            Coverage = new CoverageStats { TotalElements = 0, UnmappedElements = 0, ExcludedElements = 0, CoveragePercent = 100 },
            Executables =
            [
                new ExecutableSpec
                {
                    RefId = @"Package\Transfer Task",
                    ExecutableType = "Microsoft.TransferSqlServerObjectsTask",
                    ObjectName = "Transfer Task",
                    TransferSqlServerObjectsTask = new TransferSqlServerObjectsTaskPayload
                    {
                        SourceDatabase = "Src",
                        DestinationDatabase = "Dst",
                    },
                },
            ],
        };

        var plan = PackagePlanner.Plan(package);

        var gap = Assert.Single(plan.Gaps, g => g.Location == "Transfer Task");
        Assert.Contains("no TablesList recorded", gap.Reason);
        Assert.Contains("no flags set", gap.Reason);
    }

    [Fact]
    public void Plan_ReportsAnExplicitGap_ForAForEachLoopContainer()
    {
        // Real fixture (object-model-built, see docs/report-schema.md's ForEach/Script Task
        // section), not hand-rolled -- a ForEach Loop's own children (a Script Task here) must
        // NOT be silently flattened and run once each: that would be wrong, not just
        // incomplete, since Etl.Core has no per-iteration loop abstraction to generate against.
        var package = LoadSyntheticFixture("SyntheticForEachScript.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.PreLoadStatements);
        Assert.Empty(plan.Flows);
        var gap = Assert.Single(plan.Gaps);
        Assert.Equal("FELC_Files", gap.Location);
        Assert.Contains("ForEach Loop", gap.Reason);
    }

    [Fact]
    public void Plan_FindsTheDataConversionComponent_ForTheSyntheticDataConversionFixture()
    {
        var package = LoadSyntheticFixture("SyntheticDataConversion.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        var flow = Assert.Single(plan.Flows);
        Assert.Equal("DFT_DataConversionDemo", flow.TaskName);
        Assert.Equal("DCONV_Types", flow.DataConversion?.Name);
        Assert.Null(flow.DerivedColumn); // this fixture has no Derived Column at all -- proves the widened gate
        Assert.Equal(2, flow.DataConversion?.DataConvert?.Columns.Count);
    }

    [Fact]
    public void Plan_FindsTheCopyMapComponent_ForTheSyntheticCopyMapFixture()
    {
        // Phase 1 of the unsupported-component-types plan.
        var package = LoadSyntheticFixture("SyntheticCopyMap.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        var flow = Assert.Single(plan.Flows);
        Assert.Equal("DFT_CopyMapDemo", flow.TaskName);
        Assert.Equal("CPY_FullName", flow.CopyMap?.Name);
        Assert.Null(flow.DerivedColumn); // this fixture has no Derived Column at all
        Assert.Null(flow.DataConversion); // nor any Data Conversion
        Assert.Equal(1, flow.CopyMap?.CopyMap?.Columns.Count);
        Assert.Equal("FullNameCopy", flow.CopyMap?.CopyMap?.Columns[0].OutputColumnName);
    }

    [Fact]
    public void Plan_ReportsAnExplicitGap_ForADataFlowTaskWithMultipleSourceComponents()
    {
        // SyntheticParallelShapes.dtsx's own DFT_DirectCopy: two INDEPENDENT OLE DB
        // Source -> Destination pairs in one Data Flow Task, no Derived Column/Data Conversion
        // anywhere. Discovered as a real, previously-SILENT failure class testing this tool
        // against RBC_Demo_ETL's own DFT_SortAndMergeJoin (a Merge Join over two Flat File
        // Sources) -- before this gate, PlanDataFlow silently picked just the FIRST source
        // component found, built a row type from only ITS columns, and reported no gap at all;
        // the flow only failed at BUILD time with a confusing "row type has no definition for"
        // error once Data Conversion support (2026-08-28) happened to remove the ONE OTHER
        // reason this flow used to fail to translate. This fixture's own two-source shape used
        // to be caught one step later, by the pre-existing "no Derived Column found" gate --
        // still correctly gapped either way, just with a clearer, earlier reason now.
        var package = DtsxPackageReader.Read(Path.Combine(SsisProjectDir, "SyntheticParallelShapes.dtsx"), noRedact: false);

        var plan = PackagePlanner.Plan(package);

        Assert.DoesNotContain(plan.Flows, f => f.TaskName == "DFT_DirectCopy");
        var gap = Assert.Single(plan.Gaps, g => g.Location == "DFT_DirectCopy");
        Assert.Contains("2 source components", gap.Reason);
    }

    [Fact]
    public void Plan_FindsThePreLoadStatementAndTheOneFlow_ForTheRealLoadEmployeesPackage()
    {
        var package = TestFixtures.LoadPackage("LoadEmployees.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        // Emitter rewrite phase 2: no longer hoisted -- an ordinary SqlStep, first in plan.Steps
        // since it has no predecessor.
        var truncateStep = Assert.Single(plan.Steps.OfType<SqlStep>());
        Assert.Equal("SQL_TruncateTarget", truncateStep.TaskName);
        Assert.Equal("TRUNCATE TABLE dbo.Employee;", truncateStep.Sql);
        Assert.Empty(plan.PreLoadStatements);

        var flow = Assert.Single(plan.Flows);
        Assert.Equal("DFT_LoadEmployees", flow.TaskName);
        Assert.Equal("FF_SRC_Employees", flow.FlatFileSource?.Name);
        Assert.Equal("DER_MergeColumns", flow.DerivedColumn?.Name);
        Assert.Equal("OLEDST_Employee", flow.DestinationComponent.Name);
    }

    [Fact]
    public void Plan_FindsThePreLoadStatementAndBothFlowsInOrder_ForTheRealLoadReferenceDataPackage()
    {
        var package = TestFixtures.LoadPackage("LoadReferenceData.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        // Emitter rewrite phase 2: no longer hoisted -- an ordinary SqlStep, first in plan.Steps
        // since it has no predecessor.
        var truncateStep = Assert.Single(plan.Steps.OfType<SqlStep>());
        Assert.Equal("SQL_TruncateTargets", truncateStep.TaskName);
        Assert.Equal("TRUNCATE TABLE dbo.Department; TRUNCATE TABLE dbo.Designation;", truncateStep.Sql);
        Assert.Empty(plan.PreLoadStatements);

        Assert.Equal(2, plan.Flows.Count);
        Assert.Equal("DFT_LoadDepartment", plan.Flows[0].TaskName);
        Assert.Equal("OLEDST_Department", plan.Flows[0].DestinationComponent.Name);
        Assert.Equal("DFT_LoadDesignation", plan.Flows[1].TaskName);
        Assert.Equal("OLEDST_Designation", plan.Flows[1].DestinationComponent.Name);
    }

    [Fact]
    public void Plan_RoutesAnExecuteSqlTaskAfterADataFlow_IntoStepsNotPreLoadStatements()
    {
        // SyntheticParallelShapes.dtsx Branch 1: SQL_CreateLoadATemp -> DFT_LoadA -> SQL_UpdateLoadA.
        // Only the first is genuinely pre-load; the second has an incoming constraint FROM the
        // data flow (SyntheticParallelShapesTests.Branch1_ExecuteSqlRunsAfterTheDataFlow_NotOnlyBefore
        // already proves that at the reader level -- this proves the planner now honors it).
        var package = DtsxPackageReader.Read(Path.Combine(SsisProjectDir, "SyntheticParallelShapes.dtsx"), noRedact: false);

        var plan = PackagePlanner.Plan(package);

        // Emitter rewrite phase 2: no longer hoisted into a separate list -- SQL_CreateLoadATemp
        // is genuinely pre-flow, so it's the ordinary SqlStep sitting BEFORE the flow in plan.Steps.
        Assert.Contains(plan.Steps.OfType<SqlStep>(), s => s.TaskName == "SQL_CreateLoadATemp" && s.Sql ==
            "IF OBJECT_ID('dbo.SyntheticLoadATemp') IS NULL\nCREATE TABLE dbo.SyntheticLoadATemp (ID INT NOT NULL, Name NVARCHAR(50) NOT NULL, Amount DECIMAL(12,2) NOT NULL, EntryDate DATE NOT NULL);");
        Assert.Empty(plan.PreLoadStatements);

        var createStepIndex = plan.Steps.FindIndex(s => s is SqlStep sql && sql.TaskName == "SQL_CreateLoadATemp");
        var flowStepIndex = plan.Steps.FindIndex(s => s is FlowStep flow && flow.Flow.TaskName == "DFT_LoadA");
        var sqlStepIndex = plan.Steps.FindIndex(s => s is SqlStep sql && sql.TaskName == "SQL_UpdateLoadA");
        Assert.True(createStepIndex >= 0, "SQL_CreateLoadATemp should be a SqlStep in plan.Steps");
        Assert.True(flowStepIndex >= 0, "DFT_LoadA should be a FlowStep in plan.Steps");
        Assert.True(sqlStepIndex >= 0, "SQL_UpdateLoadA should be a SqlStep in plan.Steps");
        Assert.True(createStepIndex < flowStepIndex, "SQL_CreateLoadATemp must come before DFT_LoadA");
        Assert.True(sqlStepIndex > flowStepIndex, "SQL_UpdateLoadA must come after DFT_LoadA");

        var sqlStep = (SqlStep)plan.Steps[sqlStepIndex];
        Assert.Equal("UPDATE dbo.SyntheticLoadATemp SET EntryDate = EntryDate;", sqlStep.Sql);
    }

    [Fact]
    public void Plan_ResolvesConnectionManagerName_ForAnExecuteSqlTask()
    {
        // SyntheticSecondConnectionSql.dtsx: SQL_PreLoad -> DFT_Load -> SQL_CacheSet_SecondDb,
        // the latter targeting a SECOND connection manager (CM_SqlSecondDb) -- real bug found
        // running RBC_Demo_ETL's own Package_Legacy.dtsx end to end. PackagePlanner itself
        // doesn't decide primary-vs-secondary (it doesn't know the package's resolved target
        // database yet -- see SqlStep's own doc comment) -- it just needs to carry the raw
        // resolved connection manager name through, always, for PackageGenerator to act on
        // once flows have resolved a primary.
        var package = LoadSyntheticFixture("SyntheticSecondConnectionSql.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        var sqlStep = Assert.IsType<SqlStep>(plan.Steps.Single(s => s is SqlStep sql && sql.TaskName == "SQL_CacheSet_SecondDb"));
        Assert.Equal("CM_SqlSecondDb", sqlStep.ConnectionManagerName);
    }

    [Fact]
    public void Plan_ResolvesTwoExpressionTaskAssignments_ForTheSyntheticExpressionTaskFixture()
    {
        // SyntheticExpressionTask.dtsx: EXPR_SetCutoff (DATEADD("Minute",-5,GETUTCDATE()) into a
        // DateTime variable, the real evidenced call from DailyETLMain.dtsx) -> EXPR_SetTableName
        // (a plain string literal, "City" -- the real shape of every OTHER ExpressionTask in that
        // same package) -> DFT_Load -> a guarded SQL_LogIfTableNameSet. Phase 2 of the
        // unsupported-component-types plan.
        var package = LoadSyntheticFixture("SyntheticExpressionTask.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);

        var setCutoff = Assert.Single(plan.Steps.OfType<ExpressionStep>(), s => s.TaskName == "EXPR_SetCutoff");
        Assert.Equal("User::TargetETLCutoffTime", setCutoff.SsisVariableName);
        Assert.Equal("DateTime.UtcNow.AddMinutes(-(5))", setCutoff.CSharpValueExpression);

        var setTableName = Assert.Single(plan.Steps.OfType<ExpressionStep>(), s => s.TaskName == "EXPR_SetTableName");
        Assert.Equal("User::TableName", setTableName.SsisVariableName);
        Assert.Equal("\"City\"", setTableName.CSharpValueExpression);

        // The downstream guard reads back the SAME variable name an ExpressionTask writes --
        // proves the two features (ExpressionTask, conditional-constraint guard) resolve against
        // the identical GuardVariable table, not two independently-derived namings.
        Assert.Contains(plan.Steps, s => s.Guard is { CSharpPredicate: var p } && p.Contains("User::TableName"));
    }

    [Fact]
    public void Plan_ResolvesTheRealTrimMillisecondsExpression_AndSeedsTheSelfReferencedVariable()
    {
        // SyntheticExpressionTaskMillisecond.dtsx, Phase 6: EXPR_SetMs789 -> EXPR_TrimMilliseconds
        // (the real evidenced DailyETLMain.dtsx "Trim Any Milliseconds" call, verbatim) ->
        // EXPR_ObserveMs -> DFT_Load -> a guarded SQL_LogIfTrimmedToZero. Also proves the real bug
        // this round found and fixed: TargetETLCutoffTime is read by its OWN assignment's RHS
        // (never by any guard), so it needs a design-time seed of its own -- previously only a
        // guard-read variable (MsAfterTrim here) ever got one, and this variable would otherwise
        // throw "was never set" the first time EXPR_TrimMilliseconds actually ran.
        var package = LoadSyntheticFixture("SyntheticExpressionTaskMillisecond.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);

        var trim = Assert.Single(plan.Steps.OfType<ExpressionStep>(), s => s.TaskName == "EXPR_TrimMilliseconds");
        Assert.Equal("User::TargetETLCutoffTime", trim.SsisVariableName);
        Assert.Equal(
            "SsisFn.DateAddMillisecond(packageVariables.GetRequired<DateTime>(\"User::TargetETLCutoffTime\"), " +
            "(0) - (SsisFn.DatePartMillisecond(packageVariables.GetRequired<DateTime>(\"User::TargetETLCutoffTime\"))))",
            trim.CSharpValueExpression);

        var observe = Assert.Single(plan.Steps.OfType<ExpressionStep>(), s => s.TaskName == "EXPR_ObserveMs");
        Assert.Equal("SsisFn.DatePartMillisecond(packageVariables.GetRequired<DateTime>(\"User::TargetETLCutoffTime\"))", observe.CSharpValueExpression);

        Assert.Contains(plan.VariableSeeds, s => s.SsisName == "User::TargetETLCutoffTime" && s.ClrTypeName == "DateTime");
        Assert.Contains(plan.VariableSeeds, s => s.SsisName == "User::MsAfterTrim");
    }

    [Fact]
    public void Plan_ResolvesAConditionalSplitsBranches_InEvaluationOrder_DefaultLast()
    {
        // SyntheticConditionalSplit.dtsx: OLE DB Source -> Derived Column (LoadedAtUtc) ->
        // Conditional Split (one case, "Amount > 1000" -> HighValue; default -> LowValue) -> 2
        // OLE DB Destinations. Unlike SyntheticLookupSplit.dtsx (whose Conditional Split is
        // masked by its own Lookup at the PackageGenerator level), this fixture has no Lookup,
        // so PlanConditionalSplit's own branch resolution is what's under test here.
        var package = LoadSyntheticFixture("SyntheticConditionalSplit.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        var flow = Assert.Single(plan.Flows);
        Assert.Equal("DFT_ConditionalSplitDemo", flow.TaskName);
        Assert.NotNull(flow.ConditionalSplit);

        var branches = flow.ConditionalSplit!.Branches;
        Assert.Equal(2, branches.Count);

        Assert.Equal("HighValue", branches[0].OutputName);
        Assert.Equal("Amount > 1000", branches[0].FriendlyExpression);
        Assert.Equal("OLE DB Destination", branches[0].Destination!.Name);
        Assert.Equal("[dbo].[SyntheticHighValue]", branches[0].Destination!.OleDbDestination?.OpenRowset);

        // The default branch is always last and carries no condition of its own.
        Assert.Equal("LowValue", branches[1].OutputName);
        Assert.Null(branches[1].FriendlyExpression);
        Assert.Equal("OLE DB Destination 1", branches[1].Destination!.Name);
        Assert.Equal("[dbo].[SyntheticLowValue]", branches[1].Destination!.OleDbDestination?.OpenRowset);

        // DestinationComponent still points at the default branch's destination, kept non-null
        // for any code path that only looks at it (see DataFlowPlan's own doc comment).
        Assert.Equal(branches[1].Destination, flow.DestinationComponent);
    }

    [Fact]
    public void Plan_WalksThroughPerBranchDerivedColumnsAndAUnionAll_ToTheSameSharedDestination()
    {
        // SyntheticConditionalSplitRemerge.dtsx: OLE DB Source -> Derived Column (LoadedAtUtc,
        // shared) -> Conditional Split (one case, "Amount > 1000" -> High; default -> Low) ->
        // each branch through its OWN Derived Column (Segment <- "High"/"Low") -> Union All ->
        // ONE shared OLE DB Destination. Confirmed beyond this test by actually building and
        // running the generated exe against .\SQLFORPOC_2022 -- see
        // Tools/SsisExtractor/CLAUDE.md's "Conditional Split -- multi-hop Union All remerge"
        // section (this fixture itself is not confirmed dtexec-executable, an unrelated,
        // documented SSIS object-model quirk in how it was built; ssisx never depends on
        // that, and the generated C# was verified end to end regardless).
        var package = LoadSyntheticFixture("SyntheticConditionalSplitRemerge.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        var flow = Assert.Single(plan.Flows);
        Assert.NotNull(flow.ConditionalSplit);

        var branches = flow.ConditionalSplit!.Branches;
        Assert.Equal(2, branches.Count);

        var highBranch = Assert.Single(branches, b => b.OutputName == "High");
        Assert.Equal("Amount > 1000", highBranch.FriendlyExpression);
        var highTag = Assert.Single(highBranch.DerivedColumns);
        Assert.Equal("DER_TagHigh", highTag.Name);

        var lowBranch = Assert.Single(branches, b => b.OutputName == "Low");
        Assert.Null(lowBranch.FriendlyExpression);
        var lowTag = Assert.Single(lowBranch.DerivedColumns);
        Assert.Equal("DER_TagLow", lowTag.Name);

        // Both branches pass through the SAME Union All ('UNION_Recombine') to the SAME
        // destination -- resolved structurally by the forward walk, no special-casing of the
        // convergence needed.
        Assert.Same(highBranch.Destination, lowBranch.Destination);
        Assert.Equal("[dbo].[SyntheticRemergeTarget]", highBranch.Destination!.OleDbDestination?.OpenRowset);
    }

    [Fact]
    public void Plan_ResolvesFileSystemTasksInBothPositions_PreLoadAndPostFlow()
    {
        // SyntheticFileSystemTask.dtsx: SQL_PreLoad (TRUNCATE) -> FST_PreLoadCopy (pre-load) ->
        // DFT_Load -> FST_PostLoadCopy (post-flow). Confirmed beyond this test by actually
        // building and running the generated exe against .\SQLFORPOC_2022 and reading both
        // archived files plus the target table back afterward -- see
        // Tools/SsisExtractor/CLAUDE.md's "File System Task" section.
        var package = LoadSyntheticFixture("SyntheticFileSystemTask.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        // Emitter rewrite phase 2: no longer hoisted -- SQL_PreLoad is an ordinary SqlStep, first
        // in plan.Steps since it has no predecessor.
        var truncateStep = Assert.Single(plan.Steps.OfType<SqlStep>());
        Assert.Equal("SQL_PreLoad", truncateStep.TaskName);
        Assert.Equal("TRUNCATE TABLE dbo.SyntheticFileSystemTaskTarget;", truncateStep.Sql);
        Assert.Empty(plan.PreLoadStatements);
        Assert.Empty(plan.PreLoadFileActions);

        // Both File System Tasks are now ordinary FileSystemSteps, at their true position --
        // FST_PreLoadCopy before DFT_Load, FST_PostLoadCopy after it.
        var fsSteps = plan.Steps.OfType<FileSystemStep>().ToList();
        Assert.Equal(2, fsSteps.Count);

        var preLoadStep = fsSteps.Single(s => s.TaskName == "FST_PreLoadCopy");
        Assert.Equal("Copy", preLoadStep.Action.Operation);
        Assert.EndsWith("source.txt", preLoadStep.Action.SourcePath);
        Assert.EndsWith("archived-preload.txt", preLoadStep.Action.DestinationPath);
        Assert.True(preLoadStep.Action.Overwrite);

        var postFlowStep = fsSteps.Single(s => s.TaskName == "FST_PostLoadCopy");
        Assert.Equal("Copy", postFlowStep.Action.Operation);
        Assert.EndsWith("archived-postflow.txt", postFlowStep.Action.DestinationPath);

        var preLoadIndex = plan.Steps.FindIndex(s => s is FileSystemStep fs && fs.TaskName == "FST_PreLoadCopy");
        var flowIndex = plan.Steps.FindIndex(s => s is FlowStep);
        var postFlowIndex = plan.Steps.FindIndex(s => s is FileSystemStep fs && fs.TaskName == "FST_PostLoadCopy");
        Assert.True(preLoadIndex < flowIndex, "FST_PreLoadCopy must come before the flow");
        Assert.True(postFlowIndex > flowIndex, "FST_PostLoadCopy must come after the flow");
    }

    [Fact]
    public void Plan_ResolvesAFlatFileDestination_AsAValidDestination_WithNoDerivedColumnRequired()
    {
        // SyntheticFlatFileDestination.dtsx's own DFT_ExportDelimited/DFT_ExportFixedWidth are
        // both direct copies (OLE DB Source -> Flat File Destination, no Derived Column at all)
        // -- the exact real evidenced shape of RBC_Demo_ETL's own Package_Exports.dtsx. Proves
        // PackagePlanner recognizes Microsoft.FlatFileDestination as a destination at all
        // (previously it only knew OLE DB/ADO NET Destination) without needing PackageGenerator's
        // own separate "no Derived Column" exemption to even be reached.
        var package = LoadSyntheticFixture("SyntheticFlatFileDestination.dtsx");

        var plan = PackagePlanner.Plan(package);

        // This fixture's three root flows carry no precedence constraint between them, so SSIS
        // ran them concurrently -- the emitter rewrite's phase 7 now generates real concurrency
        // for that shape too (see PackageStep.Wave), rather than reporting a "flattened to
        // sequential" advisory the way it used to. Neither flat-file flow gaps.
        Assert.Empty(plan.Gaps);
        Assert.Equal(3, plan.Flows.Count);

        var delimitedFlow = Assert.Single(plan.Flows, f => f.TaskName == "DFT_ExportDelimited");
        Assert.Equal("Microsoft.FlatFileDestination", delimitedFlow.DestinationComponent.ComponentClassId);
        Assert.Null(delimitedFlow.DerivedColumn);

        var fixedWidthFlow = Assert.Single(plan.Flows, f => f.TaskName == "DFT_ExportFixedWidth");
        Assert.Equal("Microsoft.FlatFileDestination", fixedWidthFlow.DestinationComponent.ComponentClassId);
        Assert.Null(fixedWidthFlow.DerivedColumn);
    }

    [Fact]
    public void Plan_WalksThroughSortAndMerge_ToResolveBothConditionalSplitBranchesToTheSameDestination()
    {
        // SyntheticSortMergeRemerge.dtsx's own DFT_SortMergeRemerge is the real evidenced shape
        // of RBC_Demo_ETL's own DFT_MergeSortedBranches: Conditional Split -> Sort -> Sort ->
        // Merge -> one shared destination, no per-branch transform at all. Before this round,
        // ResolveBranch's own forward walk only recognized Derived Column/Union All as
        // pass-throughs -- a Sort or Merge in the chain was a fatal "not supported" gap.
        var package = LoadSyntheticFixture("SyntheticSortMergeRemerge.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        var flow = Assert.Single(plan.Flows);
        Assert.NotNull(flow.ConditionalSplit);
        Assert.Equal(2, flow.ConditionalSplit!.Branches.Count);

        // Both branches pass through their own Sort (contributing nothing to DerivedColumns,
        // the same "pure pass-through" treatment Union All already got) and converge on the
        // SAME Merge component's single output -- so both resolve to the identical destination
        // instance, exactly like two branches sharing a Union All.
        var canada = Assert.Single(flow.ConditionalSplit.Branches, b => b.OutputName == "Canada");
        var restOfWorld = Assert.Single(flow.ConditionalSplit.Branches, b => b.OutputName == "RestOfWorld");
        Assert.Empty(canada.DerivedColumns);
        Assert.Empty(restOfWorld.DerivedColumns);
        Assert.Same(canada.Destination, restOfWorld.Destination);
        Assert.Equal("Microsoft.OLEDBDestination", canada.Destination!.ComponentClassId);
    }

    [Fact]
    public void Plan_ResolvesAForEachFileLoop_WhoseBodyIsASingleExpressionDrivenExecuteSqlTask()
    {
        // SyntheticForEachFileLoop.dtsx's own FEL_SampleFiles is the real evidenced shape of
        // RBC_Demo_ETL's own FEL_SampleFiles: a Microsoft.ForEachFileEnumerator loop whose single
        // body task's SqlStatementSource is entirely driven by a PropertyExpression referencing
        // the loop's own mapped variable. Before this round, ANY STOCK:FOREACHLOOP reported its
        // own unconditional "not supported" gap.
        var package = LoadSyntheticFixture("SyntheticForEachFileLoop.dtsx");

        var plan = PackagePlanner.Plan(package);

        // This fixture's two root executables (DFT_Load and FEL_SampleFiles) carry no precedence
        // constraint between them, so SSIS ran them concurrently -- now generated as real
        // concurrency (see PackageStep.Wave) rather than a "flattened to sequential" advisory.
        // No ForEach-Loop-specific gap either.
        Assert.Empty(plan.Gaps);
        var loopStep = Assert.Single(plan.Steps, s => s is ForEachFileLoopStep);
        var loop = ((ForEachFileLoopStep)loopStep).Loop;

        Assert.Equal("FEL_SampleFiles", loop.TaskName);
        Assert.EndsWith("synthetic-foreach-file-loop-files", loop.Folder);
        Assert.Equal("*.txt", loop.FileSpec);
        Assert.False(loop.Recurse);
        Assert.Equal(1, loop.NameRetrievalTypeRaw); // Name and extension -- the real evidenced value
        Assert.Equal("User::CurrentFile", loop.VariableName);
        Assert.Equal("SQL_LogFileName", loop.InnerTaskName);
        Assert.Contains("@[User::CurrentFile]", loop.SqlTemplate);
    }

    [Fact]
    public void Plan_ResolvesAForEachDataFlowLoop_WhoseBodyIsAWholeDataFlowTask()
    {
        // SyntheticForEachDataFlowLoop.dtsx, built speculatively 2026-08-30 (zero real evidenced
        // package anywhere in the tracked portfolio has this shape): a Microsoft.
        // ForEachFileEnumerator loop whose single body executable is a Data Flow Task (Flat File
        // Source, its own connection manager's ConnectionString entirely driven by a
        // PropertyExpression referencing the loop's own mapped variable -> Derived Column ->
        // OLE DB Destination). Before this round, ANY STOCK:FOREACHLOOP body other than a single
        // Execute SQL Task reported an unconditional "not supported" gap.
        var package = LoadSyntheticFixture("SyntheticForEachDataFlowLoop.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        Assert.Empty(plan.Flows); // deliberately NOT duplicated into plan.Flows -- see PackagePlan.Flows' own doc comment
        var loopStep = Assert.Single(plan.Steps, s => s is ForEachDataFlowLoopStep);
        var loop = ((ForEachDataFlowLoopStep)loopStep).Loop;

        Assert.Equal("FEL_SampleFiles", loop.TaskName);
        Assert.EndsWith("synthetic-foreach-data-flow-loop-files", loop.Folder);
        Assert.Equal("*.csv", loop.FileSpec);
        Assert.False(loop.Recurse);
        Assert.Equal(0, loop.NameRetrievalTypeRaw); // fully qualified -- see this fixture's own doc comment
        Assert.Equal("User::CurrentFile", loop.VariableName);
        Assert.Equal("@[User::CurrentFile]", loop.FilePathExpression);
        Assert.Equal("DFT_Load", loop.Flow.TaskName);
        Assert.NotNull(loop.Flow.FlatFileSource);
        Assert.Equal("Microsoft.OLEDBDestination", loop.Flow.DestinationComponent.ComponentClassId);
    }

    [Fact]
    public void Plan_ReportsAGap_ForAForEachDataFlowLoop_WhoseFlatFileSourceHasNoPerIterationExpression()
    {
        // SyntheticForEachDataFlowLoopStaticPath.dtsx: a byte-preserving derived copy of
        // SyntheticForEachDataFlowLoop.dtsx with its Flat File connection manager's own
        // PropertyExpression removed -- every iteration would try to read the same (non-existent)
        // static path, which is very likely not intended; reported as an explicit gap rather than
        // silently generating code that fails at runtime.
        var package = LoadSyntheticFixture("SyntheticForEachDataFlowLoopStaticPath.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Steps);
        var gap = Assert.Single(plan.Gaps, g => g.Location == "FEL_SampleFiles");
        Assert.Contains("CM_CurrentFileCsv", gap.Reason);
        Assert.Contains("no per-iteration expression", gap.Reason);
    }

    [Fact]
    public void Plan_ResolvesAForLoop_WhoseBodyIsASingleDataFlowTask()
    {
        // SyntheticForLoop.dtsx -- Phase 3 of the unsupported-component-types plan. Confirmed real
        // shape from a genuine SSDT-authored package (D:\PoC\SSIS_Packages_From_GitHub\
        // ETL-SSIS-Real-Scenarios\UseCase_34\...\Package.dtsx, "For Loop Container"): a
        // STOCK:FORLOOP whose own InitExpression/EvalExpression/AssignExpression use a BARE @Name
        // form (@Part =1 / @Part <11 / @Part = @Part + 1). This fixture reproduces that shape with
        // a small, deliberately verifiable range (Init=1, Eval=@Part<4, Assign=@Part=@Part+1).
        // Before this round, ANY STOCK:FORLOOP was unrecognized -- it fell through to WalkContainer's
        // own generic "unsupported executable type" gap.
        var package = LoadSyntheticFixture("SyntheticForLoop.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        Assert.Empty(plan.Flows); // deliberately NOT duplicated into plan.Flows -- mirrors ForEachDataFlowLoopStep
        var loopStep = Assert.Single(plan.Steps, s => s is ForLoopStep);
        var loop = ((ForLoopStep)loopStep).Loop;

        Assert.Equal("For Loop Container", loop.TaskName);
        Assert.Equal("User::Part", loop.CounterVariableName);
        Assert.Equal("int", loop.CounterClrTypeName);
        Assert.Equal("1", loop.InitCSharpExpression);
        Assert.Equal("(packageVariables.GetRequired<int>(\"User::Part\") < 4)", loop.EvalCSharpPredicate);
        Assert.Equal("(packageVariables.GetRequired<int>(\"User::Part\")) + (1)", loop.AssignCSharpValueExpression);
        Assert.Equal("DFT_Load", loop.Flow.TaskName);
        Assert.NotNull(loop.Flow.FlatFileSource);
        Assert.Equal("Microsoft.OLEDBDestination", loop.Flow.DestinationComponent.ComponentClassId);
        Assert.Contains("@[User::Part]", loop.FilePathExpression);
    }

    [Fact]
    public void Plan_ResolvesAnAggregate_WithOneGroupByColumnAndOneCountColumn()
    {
        // SyntheticAggregate.dtsx, built speculatively 2026-08-30 (RBC_Demo_ETL's own real
        // instance, AGG_ByRegion, sits downstream of a Lookup that already blocks the whole flow
        // regardless of Aggregate support -- confirmed twice by re-surveying the portfolio; built
        // on the user's own explicit request, not to close a real gap): OLE DB Source -> Aggregate
        // (GroupBy Region, Count(CustomerID) AS CustomerCount -- the exact real evidenced
        // AggregationType values from AGG_ByRegion itself) -> OLE DB Destination.
        var package = LoadSyntheticFixture("SyntheticAggregate.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        var flow = Assert.Single(plan.Flows);
        Assert.NotNull(flow.Aggregate);
        Assert.Equal("AGG_ByRegion", flow.Aggregate!.Component.Name);
        var groupBy = Assert.Single(flow.Aggregate.GroupByColumns);
        Assert.Equal("Region", groupBy.OutputColumnName);
        Assert.Equal("Region", groupBy.SourceColumnName);
        var count = Assert.Single(flow.Aggregate.Functions);
        Assert.Equal("CustomerCount", count.OutputColumnName);
        Assert.Equal("CustomerID", count.SourceColumnName);
        Assert.Equal(1, count.AggregationTypeRaw);
    }

    [Fact]
    public void Plan_ReportsAGap_ForAnAggregate_UsingAnUnsupportedAggregationType()
    {
        // SyntheticAggregateUnsupportedType.dtsx: a byte-preserving derived copy of
        // SyntheticAggregate.dtsx with its own CustomerCount column's AggregationType changed
        // from 1 (Count) to 9 -- outside the full 0-7 range gap-audit Phase 3.3 (2026-09-02)
        // measured and confirmed as the live COM property's own valid range (0-7 accepted, 8+
        // REJECTED by a real object-model probe) -- reported as an explicit gap rather than
        // guessed. Was originally 3 (CountDistinct), which this same round newly supports; bumped
        // to a genuinely still-unrecognized value so this test keeps proving what it always meant
        // to prove.
        var package = LoadSyntheticFixture("SyntheticAggregateUnsupportedType.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Flows);
        var gap = Assert.Single(plan.Gaps, g => g.Location == "DFT_Aggregate");
        Assert.Contains("AGG_ByRegion", gap.Reason);
        Assert.Contains("unrecognized AggregationType", gap.Reason);
        Assert.Contains("AggregationType=9", gap.Reason);
    }

    [Fact]
    public void Plan_ResolvesEveryAggregationType_SumAverageMinimumMaximumCountDistinctCountAll()
    {
        // SyntheticAggregateFunctions.dtsx, built 2026-09-02 (gap-audit Phase 3.3), widening the
        // original GroupBy/Count-only round to every raw AggregationType value measured via a
        // real dtexec probe -- see AggregatePayload's own doc comment for the full mapping.
        // TotalRows (CountAll) is deliberately bound with NO AggregationColumnId at all, proving
        // real SSIS's own "CountAll needs no column reference" behavior end to end.
        var package = LoadSyntheticFixture("SyntheticAggregateFunctions.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        var flow = Assert.Single(plan.Flows);
        Assert.NotNull(flow.Aggregate);
        var groupBy = Assert.Single(flow.Aggregate!.GroupByColumns);
        Assert.Equal("Region", groupBy.OutputColumnName);
        Assert.Equal("Region", groupBy.SourceColumnName);
        Assert.Equal(6, flow.Aggregate.Functions.Count);

        var totalRows = flow.Aggregate.Functions.Single(f => f.OutputColumnName == "TotalRows");
        Assert.Equal(2, totalRows.AggregationTypeRaw); // CountAll
        Assert.Null(totalRows.SourceColumnName); // no AggregationColumnId at all -- a genuine COUNT(*)

        var distinct = flow.Aggregate.Functions.Single(f => f.OutputColumnName == "DistinctAmounts");
        Assert.Equal(3, distinct.AggregationTypeRaw); // CountDistinct
        Assert.Equal("Amount", distinct.SourceColumnName);

        var sum = flow.Aggregate.Functions.Single(f => f.OutputColumnName == "SumAmount");
        Assert.Equal(4, sum.AggregationTypeRaw);
        Assert.Equal("Amount", sum.SourceColumnName);

        var avg = flow.Aggregate.Functions.Single(f => f.OutputColumnName == "AverageAmount");
        Assert.Equal(5, avg.AggregationTypeRaw);

        var min = flow.Aggregate.Functions.Single(f => f.OutputColumnName == "MinAmount");
        Assert.Equal(6, min.AggregationTypeRaw);

        var max = flow.Aggregate.Functions.Single(f => f.OutputColumnName == "MaxAmount");
        Assert.Equal(7, max.AggregationTypeRaw);
    }

    [Fact]
    public void Plan_ResolvesAnExcelSource_AsADirectCopyFlow_WithNoDerivedColumnNeeded()
    {
        // SyntheticExcelSource.dtsx: Microsoft.ExcelSource (reading the real, checked-in
        // synthetic-excel-source.xlsx) -> OLE DB Destination, no transform at all -- a genuine
        // direct-copy pipeline, the exact shape RBC_Demo_ETL's own DFT_ExcelImport has. Before
        // this round, Microsoft.ExcelSource had no bespoke resolution at all (fell into the
        // generic "no source found" gap), and OLE DB/ADO NET destinations separately required a
        // Derived Column/Data Conversion regardless of source type.
        var package = LoadSyntheticFixture("SyntheticExcelSource.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        var flow = Assert.Single(plan.Flows);
        Assert.NotNull(flow.ExcelSource);
        Assert.Equal("Excel Source", flow.ExcelSource!.Name);
        Assert.Null(flow.DerivedColumn);
        Assert.Equal("Microsoft.OLEDBDestination", flow.DestinationComponent.ComponentClassId);
    }

    [Fact]
    public void Plan_ResolvesAnXmlSource_AsADirectCopyFlow_WithNoDerivedColumnNeeded()
    {
        // SyntheticXmlSource.dtsx: Microsoft.XmlSourceAdapter (discriminated via
        // UserComponentTypeName -- see XmlSourcePayload's own doc comment) reading the real,
        // checked-in synthetic-xml-source.xml/.xsd pair -> OLE DB Destination, no transform at
        // all -- a genuine direct-copy pipeline, Phase 5 of the unsupported-component-types plan.
        var package = LoadSyntheticFixture("SyntheticXmlSource.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        var flow = Assert.Single(plan.Flows);
        Assert.NotNull(flow.XmlSource);
        Assert.Equal("XML Source", flow.XmlSource!.Name);
        Assert.Null(flow.DerivedColumn);
        Assert.Equal("Microsoft.OLEDBDestination", flow.DestinationComponent.ComponentClassId);
    }

    [Fact]
    public void Plan_ResolvesAnOleDbCommand_AsTheFlowsOwnSink_WithNoDestinationComponentAtAll()
    {
        // SyntheticOleDbCommand.dtsx: OLE DB Source -> OLE DB Command, no destination component
        // at all -- the exact real evidenced shape from SSIS_From_Sandeep's own
        // Package_Advanced.dtsx (DFT_FlagCustomers). Before this round, the "no destination
        // found" search always failed for this shape.
        var package = LoadSyntheticFixture("SyntheticOleDbCommand.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        var flow = Assert.Single(plan.Flows);
        Assert.NotNull(flow.OleDbCommand);
        Assert.Equal("OLECMD_SetFlag", flow.OleDbCommand!.Component.Name);
        Assert.Equal("UPDATE dbo.SyntheticOleDbCommandTarget SET Flagged = 1 WHERE CustomerID = {0}", flow.OleDbCommand.SqlTemplate);
        Assert.Equal(["CustomerID"], flow.OleDbCommand.ParameterColumnNames);
    }

    [Fact]
    public void Plan_ResolvesAnOleDbCommandsParameterOrder_FromEachColumnsOwnParamNBinding_NotDeclarationOrder()
    {
        // SyntheticOleDbCommandReordered.dtsx (gap-audit Phase 3.1): CustomerID is declared
        // FIRST in <inputColumns> but bound to Param_1 (the SECOND '?'); NewStatus is declared
        // SECOND but bound to Param_0 (the FIRST '?') -- confirmed real via a live SSIS
        // object-model probe (2026-09-02) that ParameterMapping is never populated and real
        // binding is carried entirely by each column's own externalMetadataColumnId. Before
        // this fix, ResolveOleDbCommand used raw declaration order and would have silently
        // produced ["CustomerID", "NewStatus"] here -- binding the wrong value to each '?'.
        var package = LoadSyntheticFixture("SyntheticOleDbCommandReordered.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps.Where(g => g.Location.Contains("OLECMD", StringComparison.Ordinal)));
        var flow = Assert.Single(plan.Flows);
        Assert.NotNull(flow.OleDbCommand);
        Assert.Equal(
            "UPDATE dbo.SyntheticOleDbCommandReorderedTarget SET StatusCode = {0} WHERE CustomerID = {1}",
            flow.OleDbCommand!.SqlTemplate);
        Assert.Equal(["NewStatus", "CustomerID"], flow.OleDbCommand.ParameterColumnNames);
    }

    [Fact]
    public void Plan_ResolvesAnOleDbCommandsParameterOrder_ForAnExecStoredProcedureCall_ByExternalColumnListPosition_NotByName()
    {
        // SyntheticOleDbCommandExecNamed.dtsx: reproduces the REAL evidenced shape from
        // SSIS_From_Sandeep's own Package_Advanced.dtsx (DFT_FlagCustomers, EXEC
        // dbo.usp_SetCustomerFlag ?, N'flagged') -- an EXEC stored-procedure call whose bound
        // external columns are named after the procedure's own parameters ("@CustomerID"), not
        // "Param_N". A first fix (parsing a Param_N numeric suffix) regressed this exact real
        // shape; the actual fix resolves order from each bound external column's own position
        // within the input's <externalMetadataColumns> list, which stays correct here too. The
        // fixture's own <inputColumns> declares NewStatus first, CustomerID second (the OPPOSITE
        // of the procedure's own @CustomerID, @NewStatus declaration) specifically to prove
        // declaration order is not what's being used.
        var package = LoadSyntheticFixture("SyntheticOleDbCommandExecNamed.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps.Where(g => g.Location.Contains("OLECMD", StringComparison.Ordinal)));
        var flow = Assert.Single(plan.Flows);
        Assert.NotNull(flow.OleDbCommand);
        Assert.Equal("EXEC dbo.usp_SyntheticSetStatus {0}, {1}", flow.OleDbCommand!.SqlTemplate);
        Assert.Equal(["CustomerID", "NewStatus"], flow.OleDbCommand.ParameterColumnNames);
    }

    [Fact]
    public void Plan_ResolvesAMidChainRowCount_IntoTheFlowsOwnRowCountsList()
    {
        // SyntheticRowCountVariable.dtsx (gap-audit Phase 3.2): OLE DB Source -> RowCount
        // (User::RowsLoaded, a LIVE mid-chain passthrough, not the already-handled discarded-
        // dead-end shape) -> OLE DB Destination. Before this round, a live RowCount silently
        // generated as an inert passthrough (TransformEmitter.RecognizedPassthroughComponentClassIds
        // already treats it as safe-to-guess for COLUMN resolution) with its own side effect --
        // the row count itself -- dropped with no gap reported at all.
        var package = LoadSyntheticFixture("SyntheticRowCountVariable.dtsx");

        var plan = PackagePlanner.Plan(package);

        var flow = Assert.Single(plan.Flows);
        var rowCount = Assert.Single(flow.RowCounts ?? []);
        Assert.Equal("RC_RowsLoaded", rowCount.Component.Name);
        Assert.Equal("User::RowsLoaded", rowCount.VariableName);
    }

    [Fact]
    public void Plan_ReportsANamedGap_WhenAnOleDbCommandInputColumnHasNoResolvableParamNBinding()
    {
        // Byte-preserving derivative of SyntheticOleDbCommand.dtsx: its one input column's own
        // externalMetadataColumnId is retargeted at a nonexistent external column ("NoSuchParam"
        // instead of "Param_0"), so BuildColumnMappings can never resolve it -- proving the gap
        // path fires instead of silently falling back to a guessed order.
        var package = LoadSyntheticFixture("SyntheticOleDbCommandUnmappedParam.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Flows);
        Assert.Contains(plan.Gaps, g => g.Reason.Contains("no resolvable placeholder binding", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_ResolvesAMulticast_FanningToTwoDifferentDestinationKinds_SkippingTheDanglingSpareOutput()
    {
        // SyntheticMulticast.dtsx: OLE DB Source -> Multicast -> {OLE DB Destination, Flat File
        // Destination} -- the exact real evidenced shape from SSIS_From_Sandeep's own
        // Package_Legacy.dtsx (DFT_FixedWidthImport). The real object model auto-provisions a
        // THIRD, permanently unconnected "spare" output the moment both real branches are wired
        // up (confirmed real from both this fixture's own saved XML and the real package's own
        // MCAST_FixedRows) -- PlanMulticast must resolve exactly 2 branches, not 3.
        var package = LoadSyntheticFixture("SyntheticMulticast.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        var flow = Assert.Single(plan.Flows);
        Assert.NotNull(flow.Multicast);
        Assert.Equal(2, flow.Multicast!.Branches.Count);
        Assert.Equal("Microsoft.OLEDBDestination", flow.Multicast.Branches[0].Destination!.ComponentClassId);
        Assert.Equal("Microsoft.FlatFileDestination", flow.Multicast.Branches[1].Destination!.ComponentClassId);
    }

    [Fact]
    public void Plan_ResolvesTheLookupThenAggregateFixture_WithOneDiscardedMulticastBranch()
    {
        // SyntheticLookupThenAggregate.dtsx (2026-09-02): Lookup (NoMatchBehavior=1/redirect) ->
        // Multicast -> {Output 1 -> Aggregate -> destination, Output 2 -> dead-end RowCount}.
        // The dead-end branch resolves as Discarded (advisory gap, not fatal), and the flow's
        // own DestinationComponent must resolve to the REAL destination even though the
        // discarded branch is the LAST one the Multicast declares (Branches[^1] alone would
        // have picked the wrong, null one before this round's destination-selection fix).
        var package = LoadSyntheticFixture("SyntheticLookupThenAggregate.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Contains(plan.Gaps, g => !g.IsBlocking && g.Reason.Contains("Multicast 'MCAST_Matched' output 'Multicast Output 2'") && g.Reason.Contains("User::MatchedRows"));
        Assert.DoesNotContain(plan.Gaps, g => g.IsBlocking);

        var flow = Assert.Single(plan.Flows);
        Assert.NotNull(flow.Lookup);
        Assert.NotNull(flow.Aggregate);
        Assert.NotNull(flow.Multicast);
        Assert.Equal(2, flow.Multicast!.Branches.Count);

        var discarded = Assert.Single(flow.Multicast.Branches, b => b.Discarded);
        Assert.Null(discarded.Destination);
        var live = Assert.Single(flow.Multicast.Branches, b => !b.Discarded);
        Assert.NotNull(live.Destination);
        Assert.Equal("Microsoft.OLEDBDestination", live.Destination!.ComponentClassId);

        // The real destination, resolved correctly despite the discarded branch being last.
        Assert.Same(live.Destination, flow.DestinationComponent);
    }

    [Fact]
    public void Plan_ResolvesAMulticastWithADiscardedBranch_AndNoLookupOrAggregate()
    {
        // SyntheticMulticastDiscard.dtsx (2026-09-02): a plain Multicast with one live branch
        // (-> OLE DB Destination) and one discarded RowCount branch -- proves discard
        // recognition independently of the Lookup+Aggregate composed shape.
        var package = LoadSyntheticFixture("SyntheticMulticastDiscard.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Contains(plan.Gaps, g => !g.IsBlocking && g.Reason.Contains("User::RowsSeen"));
        Assert.DoesNotContain(plan.Gaps, g => g.IsBlocking);

        var flow = Assert.Single(plan.Flows);
        Assert.NotNull(flow.Multicast);
        Assert.Equal(2, flow.Multicast!.Branches.Count);
        Assert.Single(flow.Multicast.Branches, b => b.Discarded);
        var live = Assert.Single(flow.Multicast.Branches, b => !b.Discarded);
        Assert.Same(live.Destination, flow.DestinationComponent);
    }

    [Fact]
    public void Plan_ResolvesAPercentageSampling_WithTwoMutuallyExclusiveBranchesInDeclaredOutputOrder()
    {
        // SyntheticPctSampling.dtsx (Phase 4 of the unsupported-component-types plan): OLE DB
        // Source -> Percentage Sampling -> {OLE DB Destination (sampled), OLE DB Destination (not
        // sampled)}. Confirmed via a live object-model probe (Ssis.Extract.FixtureBuilder's own
        // ProbePctSampling) that the component always declares exactly these two outputs, in this
        // order, right after ProvideComponentProperties() -- no dangling/spare-output quirk the
        // way Multicast/Merge have, so PlanPctSampling must resolve exactly 2 branches with no
        // filtering needed.
        var package = LoadSyntheticFixture("SyntheticPctSampling.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.DoesNotContain(plan.Gaps, g => g.IsBlocking);
        var flow = Assert.Single(plan.Flows);
        Assert.Null(flow.ConditionalSplit);
        Assert.Null(flow.Multicast);
        Assert.NotNull(flow.PctSampling);
        Assert.Equal("Sampling Selected Output", flow.PctSampling!.Sampled.OutputName);
        Assert.Equal("Sampling Unselected Output", flow.PctSampling.NotSampled.OutputName);
        Assert.NotNull(flow.PctSampling.Sampled.Destination);
        Assert.NotNull(flow.PctSampling.NotSampled.Destination);
        Assert.NotEqual(flow.PctSampling.Sampled.Destination!.RefId, flow.PctSampling.NotSampled.Destination!.RefId);
        Assert.Equal(30, flow.PctSampling.Component.PctSampling!.SamplingValue);
        Assert.Equal(424242, flow.PctSampling.Component.PctSampling.SamplingSeed);
        // DestinationComponent is the Sampled branch's own destination -- the same
        // non-authoritative-placeholder convention ConditionalSplit/Multicast already established.
        Assert.Same(flow.PctSampling.Sampled.Destination, flow.DestinationComponent);
    }

    [Fact]
    public void Plan_ResolvesAScd_WithAllFiveTrackedOutputsWiredToTheirOwnDestination_AndAdvisesOnTheDeadInferredOutput()
    {
        // SyntheticScdProbe.dtsx (Phase 7 of the unsupported-component-types plan): OLE DB Source
        // -> SCD -> one OLE DB Destination per output, all six wired (including Inferred Member
        // Updates Output, which EnableInferredMember=false makes structurally unreachable).
        var package = LoadSyntheticFixture("SyntheticScdProbe.dtsx");

        var plan = PackagePlanner.Plan(package);

        // The Inferred-output wiring is a real, named, NON-blocking advisory -- the package legally
        // wires a dead output, and this tool says so rather than silently ignoring it.
        Assert.DoesNotContain(plan.Gaps, g => g.IsBlocking);
        Assert.Contains(plan.Gaps, g => !g.IsBlocking && g.Reason.Contains("Inferred Member Updates Output", StringComparison.Ordinal));

        var flow = Assert.Single(plan.Flows);
        Assert.NotNull(flow.Scd);
        var scd = flow.Scd!;

        Assert.Equal(["EmpId"], scd.BusinessKeyColumns);
        Assert.Equal("int", scd.BusinessKeyTypes[0].ClrTypeName);
        Assert.False(scd.FailOnFixedAttributeChange);
        Assert.False(scd.UpdateChangingAttributeHistory);
        Assert.Contains("[StartDate] IS NOT NULL AND [EndDate] IS NULL", scd.ReferenceSql);
        Assert.Contains("SyntheticScdDim", scd.ReferenceSql);

        // Every measured role, correctly classified -- see ScdColumnRoleRaw's own doc comment.
        Assert.Contains(scd.Attributes, a => a.ColumnName == "LastName" && a.Role == ScdColumnRoleRaw.Changing);
        Assert.Contains(scd.Attributes, a => a.ColumnName == "Designation" && a.Role == ScdColumnRoleRaw.Historical);
        Assert.Contains(scd.Attributes, a => a.ColumnName == "FirstName" && a.Role == ScdColumnRoleRaw.Fixed);
        Assert.Equal(3, scd.Attributes.Count); // business key itself never appears here

        // Every one of the 5 tracked outputs is wired to its own destination, none via a command
        // (this probe fixture wires straight to an observation table per output).
        foreach (var branch in new[] { scd.Unchanged, scd.New, scd.FixedAttribute, scd.ChangingAttributeUpdates, scd.HistoricalAttributeInserts })
        {
            Assert.False(branch.Branch.Discarded);
            Assert.NotNull(branch.Branch.Destination);
            Assert.Null(branch.Command);
        }

        Assert.Equal(5, scd.LiveBranches().Count());
    }

    [Fact]
    public void Plan_ResolvesTheRealShapedScd_WithACommandOnlyBranch_AndACommandThenInsertBranch()
    {
        // SyntheticScd.dtsx: the real evidenced package's own chain shape -- Changing Attribute
        // Updates Output terminates in a bare OLE DB Command (Type 1 in-place UPDATE, no
        // destination); Historical Attribute Inserts Output passes through an OLE DB Command
        // (closing the old row) and THEN converges with New Output on one shared destination via
        // a Union All -> Derived Column chain. Unchanged/Fixed Attribute/Inferred Member Updates
        // are all left unwired, exactly as in the real package (it wires only 4 of 6 outputs).
        var package = LoadSyntheticFixture("SyntheticScd.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.DoesNotContain(plan.Gaps, g => g.IsBlocking);
        var flow = Assert.Single(plan.Flows);
        Assert.NotNull(flow.Scd);
        var scd = flow.Scd!;

        Assert.True(scd.Unchanged.Branch.Discarded);
        Assert.True(scd.FixedAttribute.Branch.Discarded);

        // Changing: command only, no destination -- the Type 1 in-place update IS the whole effect.
        Assert.False(scd.ChangingAttributeUpdates.Branch.Discarded);
        Assert.Null(scd.ChangingAttributeUpdates.Branch.Destination);
        Assert.NotNull(scd.ChangingAttributeUpdates.Command);
        Assert.Contains("SET [LastName] = ", scd.ChangingAttributeUpdates.Command!.SqlTemplate);

        // Historical: command AND destination -- close the old row, then insert the new one
        // through the SAME destination the New Output branch feeds (converged via Union All).
        Assert.False(scd.HistoricalAttributeInserts.Branch.Discarded);
        Assert.NotNull(scd.HistoricalAttributeInserts.Command);
        Assert.Contains("SET [EndDate] = ", scd.HistoricalAttributeInserts.Command!.SqlTemplate);
        Assert.NotNull(scd.HistoricalAttributeInserts.Branch.Destination);

        // New: insert only, and the SAME destination Historical's own insert half reaches --
        // the whole point of the Union All convergence.
        Assert.False(scd.New.Branch.Discarded);
        Assert.Null(scd.New.Command);
        Assert.NotNull(scd.New.Branch.Destination);
        Assert.Equal(scd.New.Branch.Destination!.RefId, scd.HistoricalAttributeInserts.Branch.Destination!.RefId);

        Assert.Equal(3, scd.LiveBranches().Count());
    }

    [Fact]
    public void Plan_ResolvesAScdDangling_ThirdUnionAllInput_AsFilteredOutRatherThanAGap()
    {
        // The real evidenced package's own Union All declares a genuine third input with zero
        // columns and no incoming path at all -- a permanently-unused spare slot, the same
        // "component auto-provisions a dangling extra slot" quirk already documented for
        // Microsoft.Multicast's own trailing output. PlanUnion must filter this out by
        // connectivity (an incoming path), not choke on it as an unsupported malformed side --
        // confirmed by the SyntheticScd.dtsx fixture (built via the real object model) actually
        // reproducing this shape and this same test failing before PlanUnion was fixed to filter
        // by connectivity rather than iterate every declared input.
        var package = LoadSyntheticFixture("SyntheticScd.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.DoesNotContain(plan.Gaps, g => g.IsBlocking && g.Reason.Contains("Union All Input 3", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Gaps, g => g.IsBlocking && g.Reason.Contains("has no columns", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_ResolvesAStandaloneSort_ForTheSyntheticStandaloneSortFixture()
    {
        // SyntheticStandaloneSort.dtsx (gap-audit Phase 3.5, 2026-09-02): OLE DB Source (seed
        // rows deliberately out of ID order) -> Sort (by ID, ascending) -> Flat File
        // Destination. Before this round, a standalone Sort was silently invisible to
        // PlanDataFlow -- confirmed real by generating this exact fixture before the fix
        // existed and reading the emitted Program.cs (a plain unconditional read, no ordering
        // applied, no gap reported).
        var package = LoadSyntheticFixture("SyntheticStandaloneSort.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        var flow = Assert.Single(plan.Flows);
        Assert.NotNull(flow.SortKey);
        Assert.Equal("SORT_ById", flow.SortKey!.Sort.Name);
        Assert.Equal("ID", flow.SortKey.KeyColumnName);
        Assert.Equal("int", flow.SortKey.KeyClrType.ClrTypeName);
    }

    [Fact]
    public void Plan_ReportsAGap_WhenAStandaloneSortHasMoreThanOneKey()
    {
        var package = StandaloneSortPackage(sortKeys:
        [
            new SortKeySpec { ColumnName = "ID", Position = 1 },
            new SortKeySpec { ColumnName = "Name", Position = 2 },
        ]);

        var plan = PackagePlanner.Plan(package);

        var gap = Assert.Single(plan.Gaps);
        Assert.Contains("2 sort key(s)", gap.Reason);
        Assert.Contains("only exactly one (ascending) is supported", gap.Reason);
        Assert.Empty(plan.Flows);
    }

    [Fact]
    public void Plan_ReportsAGap_WhenAStandaloneSortHasNoKeyAtAll()
    {
        var package = StandaloneSortPackage(sortKeys: []);

        var plan = PackagePlanner.Plan(package);

        var gap = Assert.Single(plan.Gaps);
        Assert.Contains("0 sort key(s)", gap.Reason);
    }

    [Fact]
    public void Plan_ReportsAGap_WhenTwoStandaloneSortsExistInTheSameFlow()
    {
        var package = StandaloneSortPackage(secondSort: true);

        var plan = PackagePlanner.Plan(package);

        var gap = Assert.Single(plan.Gaps);
        Assert.Contains("2 Sort components", gap.Reason);
        Assert.Contains("only exactly one", gap.Reason);
    }

    [Fact]
    public void Plan_ReportsAGap_WhenAStandaloneSortDoesNotLeadToTheResolvedDestination()
    {
        // The Sort component exists in the pipeline, but its own output is never connected to
        // anything -- the destination is fed directly from the source instead. Silently
        // proceeding here (i.e. just not applying the sort) would repeat the exact bug this
        // round fixes, so this must gap, never fall through unnoticed.
        var package = StandaloneSortPackage(sortConnectsToDestination: false);

        var plan = PackagePlanner.Plan(package);

        var gap = Assert.Single(plan.Gaps);
        Assert.Contains("does not lead to this flow's own resolved destination", gap.Reason);
    }

    [Fact]
    public void Plan_ResolvesAMergeInterleave_ForTheSyntheticMergeInterleaveProbeFixture()
    {
        // SyntheticMergeInterleaveProbe.dtsx (gap-audit Phase 3.6, 2026-09-02): two genuinely
        // independent OLE DB Sources (interleaved key ranges, seeded out of order) -> Sort each
        // -> Microsoft.Merge -> Flat File Destination. Real dtexec run confirmed a TRUE
        // sort-preserving interleave (1,2,3,4,5,6), not concatenation -- see
        // MergeInterleaveRowSource's own doc comment for the exact probe.
        var package = LoadSyntheticFixture("SyntheticMergeInterleaveProbe.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        var flow = Assert.Single(plan.Flows);
        Assert.NotNull(flow.Union);
        Assert.True(flow.Union!.IsSortedInterleave);
        Assert.Equal(2, flow.Union.Sides.Count);
        Assert.All(flow.Union.Sides, side =>
        {
            Assert.NotNull(side.Sort);
            Assert.NotNull(side.SortKey);
            Assert.Equal("ID", side.SortKey!.KeyColumnName);
            Assert.Equal("int", side.SortKey.KeyClrType.ClrTypeName);
        });
        Assert.Contains(flow.Union.Sides, s => s.SourceComponent.Name == "OLE DB Source Left");
        Assert.Contains(flow.Union.Sides, s => s.SourceComponent.Name == "OLE DB Source Right");
    }

    [Fact]
    public void Plan_ResolvesAUnionAll_ForTheSyntheticUnionTwoSourcesFixture()
    {
        // SyntheticUnionTwoSources.dtsx (gap-audit Phase 3.6, 2026-09-02): two genuinely
        // independent OLE DB Sources, no Sort at all -> Microsoft.UnionAll -> Flat File
        // Destination. UnionAll needs no sorted input and resolves no SortKey per side, unlike
        // Merge.
        var package = LoadSyntheticFixture("SyntheticUnionTwoSources.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        var flow = Assert.Single(plan.Flows);
        Assert.NotNull(flow.Union);
        Assert.False(flow.Union!.IsSortedInterleave);
        Assert.Equal(2, flow.Union.Sides.Count);
        Assert.All(flow.Union.Sides, side =>
        {
            Assert.Null(side.Sort);
            Assert.Null(side.SortKey);
        });
    }

    [Fact]
    public void Plan_ReportsAGap_WhenAnAggregateCoexistsWithADestinationLessOleDbCommand()
    {
        // SyntheticAggregateThenOleDbCommand.dtsx (2026-09-06) -- a real, previously-only-
        // incidentally-safe shape found by the same third independent review as the Union case
        // above: flow.Aggregate and flow.OleDbCommand used to be able to coexist on one
        // DataFlowPlan with no explicit guard here, saved only by an unrelated fast-load check
        // that happens to always fail for an OLE DB Command component (it has no destination
        // side at all). Now an explicit `aggregate is null` guard in PlanDataFlow's own OLE DB
        // Command carve-out makes this fall through to the generic "no destination found" gap
        // instead of ever building a DataFlowPlan carrying both fields.
        var package = LoadSyntheticFixture("SyntheticAggregateThenOleDbCommand.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Flows);
        var gap = Assert.Single(plan.Gaps, g => g.Location == "DFT_AggregateThenOleDbCommand");
        Assert.True(gap.IsBlocking);
        Assert.Contains("no OLE DB Destination, ADO NET Destination, Flat File Destination, or OLE DB Command found", gap.Reason);
    }

    [Fact]
    public void Plan_ReportsAGap_WhenAUnionAllCoexistsWithAWhollyUnrelatedExtraSource()
    {
        // SyntheticUnionPlusExtraSource.dtsx (2026-09-06) -- a real, previously-SILENT
        // correctness bug found by a third independent review: PlanDataFlow's own
        // MergeJoin/Union carve-out used to return PlanUnion's result unconditionally, before
        // the generic "more than one source component" gate ever ran -- so a genuinely-resolved
        // 2-source UnionAll (the SAME shape as SyntheticUnionTwoSources.dtsx above) said nothing
        // at all about a THIRD, completely unrelated OLE DB Source -> OLE DB Destination pair
        // sitting in the same Data Flow Task, which used to vanish with no file and no gap.
        var package = LoadSyntheticFixture("SyntheticUnionPlusExtraSource.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Flows);
        var gap = Assert.Single(plan.Gaps, g => g.Location == "DFT_UnionPlusExtraSource");
        Assert.True(gap.IsBlocking);
        Assert.Contains("3 source components", gap.Reason);
        Assert.Contains("only accounts for 2 of them", gap.Reason);
    }

    [Fact]
    public void Plan_DoesNotIntercept_TheDivergeThenReconvergeUnionAllShape()
    {
        // SyntheticConditionalSplitRemerge.dtsx: ONE shared upstream source -> Conditional
        // Split -> per-branch Derived Column -> Union All -> one destination -- the
        // already-closed (2026-08-27) "diverge-then-reconverge" shape, fully handled by
        // ResolveBranch's own forward pass-through walk. PlanDataFlow's own new Phase-3.6 gate
        // must NOT intercept this: it has a Conditional Split upstream, so flow.Union must stay
        // null and flow.ConditionalSplit must resolve exactly as it always has. Confirms the
        // gating decision in PlanDataFlow's own comment actually holds, not just in theory.
        var package = LoadSyntheticFixture("SyntheticConditionalSplitRemerge.dtsx");

        var plan = PackagePlanner.Plan(package);

        var flow = Assert.Single(plan.Flows);
        Assert.Null(flow.Union);
        Assert.NotNull(flow.ConditionalSplit);
    }

    [Fact]
    public void Plan_ReportsAGap_WhenAMergeHasMoreThanTwoInputs()
    {
        var package = UnionPackage("Microsoft.Merge", inputCount: 3, sortEachSide: true);

        var plan = PackagePlanner.Plan(package);

        var gap = Assert.Single(plan.Gaps);
        Assert.Contains("3 inputs", gap.Reason);
        Assert.Contains("only ever evidenced with exactly 2", gap.Reason);
        Assert.Empty(plan.Flows);
    }

    [Fact]
    public void Plan_ReportsAGap_WhenAMergeSideIsNotFedByASort()
    {
        var package = UnionPackage("Microsoft.Merge", inputCount: 2, sortEachSide: false);

        var plan = PackagePlanner.Plan(package);

        var gap = Assert.Single(plan.Gaps);
        Assert.Contains("not a Sort", gap.Reason);
        Assert.Contains("Merge requires sorted input", gap.Reason);
    }

    [Fact]
    public void Plan_ReportsAGap_WhenAUnionAllSideIsFedByADataConversion()
    {
        var package = UnionPackage("Microsoft.UnionAll", inputCount: 2, sortEachSide: false, dataConversionOnSide0: true);

        var plan = PackagePlanner.Plan(package);

        var gap = Assert.Single(plan.Gaps);
        Assert.Contains("fed by a Data Conversion", gap.Reason);
        Assert.Contains("not supported yet for a multi-independent-source Merge/UnionAll", gap.Reason);
    }

    [Fact]
    public void Plan_ResolvesAnErrorRedirectDestination_ForTheSyntheticErrorRedirectFixture()
    {
        var package = LoadSyntheticFixture("SyntheticErrorRedirect.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Gaps);
        var flow = Assert.Single(plan.Flows);
        Assert.Equal("OLEDST_Target", flow.DestinationComponent.Name);
        Assert.NotNull(flow.ErrorRedirect);
        Assert.Equal("OLEDST_Errors", flow.ErrorRedirect!.ErrorDestinationComponent.Name);
    }

    [Fact]
    public void Plan_SelectsThePrimaryDestination_RegardlessOfAlphabeticalComponentOrder()
    {
        // OLEDST_Errors sorts BEFORE OLEDST_Target alphabetically -- PipelineSpec.Components'
        // own deterministic-output ordering -- so a bare FirstOrDefault (what PlanDataFlow used
        // before SelectPrimaryDestination existed) picks the ERROR table as "the" destination.
        // Confirmed real, not hypothetical: this is exactly the failure this fixture's own name
        // was chosen to catch, found running it for the first time.
        var package = LoadSyntheticFixture("SyntheticErrorRedirect.dtsx");

        var plan = PackagePlanner.Plan(package);

        var flow = Assert.Single(plan.Flows);
        Assert.Equal("OLEDST_Target", flow.DestinationComponent.Name);
    }

    [Fact]
    public void Plan_ReportsAGap_WhenAThirdDestinationIsUnaccountedFor()
    {
        // The general defensive fix, not the redirect-specific one: a THIRD destination
        // component sitting in the same pipeline, neither the resolved primary nor its own
        // error-redirect target, must never silently vanish -- the exact bug class this whole
        // round exists to close, generalized beyond the one evidenced shape.
        var package = ErrorRedirectPackageWithExtraDestination();

        var plan = PackagePlanner.Plan(package);

        var gap = Assert.Single(plan.Gaps);
        Assert.Contains("3 destination components", gap.Reason);
        Assert.Contains("only 2 are accounted for", gap.Reason);
        Assert.Contains("ExtraDestination", gap.Reason);
    }

    /// <summary>Hand-built minimal Source -&gt; Destination(RedirectRow) -&gt; ErrorDestination
    /// shape, PLUS a completely unconnected third Microsoft.OLEDBDestination -- proves the
    /// general "unaccounted destination" gap fires even when the redirect pair itself resolves
    /// cleanly. Same "no dtsx/object-model build needed" reasoning as <see cref="UnionPackage"/>.</summary>
    private static PackageSpec ErrorRedirectPackageWithExtraDestination()
    {
        var components = new List<PipelineComponentSpec>
        {
            new()
            {
                RefId = "SRC", Name = "Source", ComponentClassId = "Microsoft.OLEDBSource",
                OleDbSource = new OleDbSourcePayload { AccessMode = 2, SqlCommand = "SELECT ID FROM dbo.Source" },
                Outputs = [new PipelineOutputSpec { RefId = "SRC.Output", Name = "Output", Columns = [new PipelineOutputColumnSpec { RefId = "SRC.Output.Columns[ID]", Name = "ID", DataType = "i4", LineageId = "LIN_ID" }] }],
            },
            new()
            {
                RefId = "DST", Name = "Destination", ComponentClassId = "Microsoft.OLEDBDestination",
                Inputs = [new PipelineInputSpec { RefId = "DST.Input", Name = "Input", ErrorRowDisposition = "RedirectRow", Columns = [new PipelineInputColumnSpec { RefId = "DST.Input.Columns[ID]", CachedName = "ID", LineageId = "LIN_ID" }] }],
                Outputs = [new PipelineOutputSpec { RefId = "DST.Error", Name = "Error Output", IsErrorOut = true, Columns = [] }],
            },
            new()
            {
                RefId = "ERR", Name = "ErrorDestination", ComponentClassId = "Microsoft.OLEDBDestination",
                Inputs = [new PipelineInputSpec { RefId = "ERR.Input", Name = "Input", Columns = [] }],
            },
            new()
            {
                RefId = "EXTRA", Name = "ExtraDestination", ComponentClassId = "Microsoft.OLEDBDestination",
                Inputs = [new PipelineInputSpec { RefId = "EXTRA.Input", Name = "Input", Columns = [] }],
            },
        };
        var paths = new List<PipelinePathSpec>
        {
            new() { RefId = "P_SRC_DST", StartId = "SRC.Output", EndId = "DST.Input" },
            new() { RefId = "P_DST_ERR", StartId = "DST.Error", EndId = "ERR.Input" },
        };

        var pipeline = new PipelineSpec { Components = components, Paths = paths };
        return new PackageSpec
        {
            ObjectName = "P",
            SourceDtsxPath = "P.dtsx",
            Sha256 = "",
            FileSizeBytes = 0,
            LastWriteTimeUtc = default,
            ProtectionLevelRaw = 0,
            ProtectionLevelName = "DontSaveSensitive",
            Coverage = new CoverageStats { TotalElements = 0, UnmappedElements = 0, ExcludedElements = 0, CoveragePercent = 100 },
            Executables =
            [
                new ExecutableSpec
                {
                    RefId = @"Package\DFT_Test",
                    ExecutableType = "Microsoft.Pipeline",
                    ObjectName = "DFT_Test",
                    DataFlowTask = new DataFlowTaskPayload { Pipeline = pipeline, Lineage = LineageBuilder.Build(pipeline) },
                },
            ],
        };
    }

    /// <summary>Hand-built minimal N-independent-source -&gt; [Sort] -&gt; Microsoft.Merge/UnionAll
    /// -&gt; Destination pipeline for gap-audit Phase 3.6's own negative-path tests -- same
    /// "no dtsx/object-model build needed" reasoning as <see cref="StandaloneSortPackage"/>. Each
    /// side is its own independent OLE DB Source (distinct SqlCommand text), never a Conditional
    /// Split branch, so PlanDataFlow's own new gate always reaches PlanUnion here.</summary>
    private static PackageSpec UnionPackage(
        string componentClassId, int inputCount, bool sortEachSide, bool dataConversionOnSide0 = false)
    {
        var components = new List<PipelineComponentSpec>();
        var paths = new List<PipelinePathSpec>();
        var unionInputs = new List<PipelineInputSpec>();

        for (var i = 0; i < inputCount; i++)
        {
            var srcRefId = $"SRC{i}";
            var srcLineageId = $"LIN_SRC{i}_ID";
            var feedRefId = srcRefId;
            var feedOutputRefId = $"{srcRefId}.Output";
            var feedLineageId = srcLineageId;

            components.Add(new PipelineComponentSpec
            {
                RefId = srcRefId,
                Name = $"Source{i}",
                ComponentClassId = "Microsoft.OLEDBSource",
                OleDbSource = new OleDbSourcePayload { AccessMode = 2, SqlCommand = $"SELECT ID FROM dbo.Side{i}" },
                Outputs = [new PipelineOutputSpec { RefId = feedOutputRefId, Name = "Output", Columns = [new PipelineOutputColumnSpec { RefId = $"{feedOutputRefId}.Columns[ID]", Name = "ID", DataType = "i4", LineageId = srcLineageId }] }],
            });

            if (i == 0 && dataConversionOnSide0)
            {
                var dcRefId = "DCONV0";
                var dcLineageId = "LIN_DCONV0_ID";
                components.Add(new PipelineComponentSpec
                {
                    RefId = dcRefId,
                    Name = "DCONV_Side0",
                    ComponentClassId = "Microsoft.DataConvert",
                    Inputs = [new PipelineInputSpec { RefId = $"{dcRefId}.Input", Name = "Input", Columns = [new PipelineInputColumnSpec { RefId = $"{dcRefId}.Input.Columns[ID]", CachedName = "ID", LineageId = srcLineageId }] }],
                    Outputs = [new PipelineOutputSpec { RefId = $"{dcRefId}.Output", Name = "Output", Columns = [new PipelineOutputColumnSpec { RefId = $"{dcRefId}.Output.Columns[ID]", Name = "ID", DataType = "i4", LineageId = dcLineageId }] }],
                });
                paths.Add(new PipelinePathSpec { RefId = $"P_SRC{i}_DC", StartId = feedOutputRefId, EndId = $"{dcRefId}.Input" });
                feedRefId = dcRefId;
                feedOutputRefId = $"{dcRefId}.Output";
                feedLineageId = dcLineageId;
            }

            if (sortEachSide)
            {
                var sortRefId = $"SORT{i}";
                var sortLineageId = $"LIN_SORT{i}_ID";
                components.Add(new PipelineComponentSpec
                {
                    RefId = sortRefId,
                    Name = $"SORT_Side{i}",
                    ComponentClassId = "Microsoft.Sort",
                    Inputs = [new PipelineInputSpec { RefId = $"{sortRefId}.Input", Name = "Input", Columns = [new PipelineInputColumnSpec { RefId = $"{sortRefId}.Input.Columns[ID]", CachedName = "ID", LineageId = feedLineageId }] }],
                    Outputs = [new PipelineOutputSpec { RefId = $"{sortRefId}.Output", Name = "Output", Columns = [new PipelineOutputColumnSpec { RefId = $"{sortRefId}.Output.Columns[ID]", Name = "ID", DataType = "i4", LineageId = sortLineageId }] }],
                    Sort = new SortPayload { Keys = [new SortKeySpec { ColumnName = "ID", Position = 1 }] },
                });
                paths.Add(new PipelinePathSpec { RefId = $"P_FEED{i}_SORT", StartId = feedOutputRefId, EndId = $"{sortRefId}.Input" });
                feedRefId = sortRefId;
                feedOutputRefId = $"{sortRefId}.Output";
                feedLineageId = sortLineageId;
            }

            var unionInputRefId = $"UNION.Inputs[{i}]";
            unionInputs.Add(new PipelineInputSpec { RefId = unionInputRefId, Name = $"Input{i}", Columns = [new PipelineInputColumnSpec { RefId = $"{unionInputRefId}.Columns[ID]", CachedName = "ID", LineageId = feedLineageId }] });
            paths.Add(new PipelinePathSpec { RefId = $"P_FEED{i}_UNION", StartId = feedOutputRefId, EndId = unionInputRefId });
        }

        components.Add(new PipelineComponentSpec
        {
            RefId = "UNION",
            Name = "UNION_Test",
            ComponentClassId = componentClassId,
            Inputs = unionInputs,
            Outputs = [new PipelineOutputSpec { RefId = "UNION.Output", Name = "Output", Columns = [new PipelineOutputColumnSpec { RefId = "UNION.Output.Columns[ID]", Name = "ID", DataType = "i4", LineageId = "LIN_UNION_ID" }] }],
        });

        var destination = new PipelineComponentSpec
        {
            RefId = "DST",
            Name = "Destination",
            ComponentClassId = "Microsoft.OLEDBDestination",
            Inputs = [new PipelineInputSpec { RefId = "DST.Input", Name = "Input", Columns = [new PipelineInputColumnSpec { RefId = "DST.Input.Columns[ID]", CachedName = "ID", LineageId = "LIN_UNION_ID" }] }],
        };
        components.Add(destination);
        paths.Add(new PipelinePathSpec { RefId = "P_UNION_DST", StartId = "UNION.Output", EndId = "DST.Input" });

        var pipeline = new PipelineSpec { Components = components, Paths = paths };

        return new PackageSpec
        {
            ObjectName = "P",
            SourceDtsxPath = "P.dtsx",
            Sha256 = "",
            FileSizeBytes = 0,
            LastWriteTimeUtc = default,
            ProtectionLevelRaw = 0,
            ProtectionLevelName = "DontSaveSensitive",
            Coverage = new CoverageStats { TotalElements = 0, UnmappedElements = 0, ExcludedElements = 0, CoveragePercent = 100 },
            Executables =
            [
                new ExecutableSpec
                {
                    RefId = @"Package\DFT_Union",
                    ExecutableType = "Microsoft.Pipeline",
                    ObjectName = "DFT_Union",
                    DataFlowTask = new DataFlowTaskPayload { Pipeline = pipeline, Lineage = LineageBuilder.Build(pipeline) },
                },
            ],
        };
    }

    /// <summary>Hand-built minimal Source(FlatFileSource) -> Sort -> Destination(OLEDBDestination)
    /// pipeline for gap-audit Phase 3.5's own negative-path tests -- no dtsx/object-model build
    /// needed, since PlanDataFlow's own multi-source/destination-search gates need only
    /// ComponentClassId + Outputs/Inputs/Paths wiring, not a full column/type-resolution-ready
    /// graph (that richness is PackageGenerator's concern, exercised separately via the real
    /// fixture in Plan_ResolvesAStandaloneSort_... above).</summary>
    private static PackageSpec StandaloneSortPackage(
        IReadOnlyList<SortKeySpec>? sortKeys = null, bool secondSort = false, bool sortConnectsToDestination = true)
    {
        var source = new PipelineComponentSpec
        {
            RefId = "SRC",
            Name = "Source",
            ComponentClassId = "Microsoft.FlatFileSource",
            Outputs = [new PipelineOutputSpec { RefId = "SRC.Output", Name = "Output", Columns = [new PipelineOutputColumnSpec { RefId = "SRC.Output.Columns[ID]", Name = "ID", DataType = "i4", LineageId = "LIN_ID" }] }],
        };

        PipelineComponentSpec BuildSort(string refId, string name, IReadOnlyList<SortKeySpec> keys, string inLineageId, string outLineageId) => new()
        {
            RefId = refId,
            Name = name,
            ComponentClassId = "Microsoft.Sort",
            Inputs = [new PipelineInputSpec { RefId = $"{refId}.Input", Name = "Input", Columns = [new PipelineInputColumnSpec { RefId = $"{refId}.Input.Columns[ID]", CachedName = "ID", LineageId = inLineageId }] }],
            Outputs = [new PipelineOutputSpec { RefId = $"{refId}.Output", Name = "Output", Columns = [new PipelineOutputColumnSpec { RefId = $"{refId}.Output.Columns[ID]", Name = "ID", DataType = "i4", LineageId = outLineageId }] }],
            Sort = new SortPayload { Keys = keys.ToList() },
        };

        var sort = BuildSort("SORT", "SORT_ById", sortKeys ?? [new SortKeySpec { ColumnName = "ID", Position = 1 }], "LIN_ID", "LIN_SORT_ID");

        var destination = new PipelineComponentSpec
        {
            RefId = "DST",
            Name = "Destination",
            ComponentClassId = "Microsoft.OLEDBDestination",
            Inputs = [new PipelineInputSpec { RefId = "DST.Input", Name = "Input", Columns = [new PipelineInputColumnSpec { RefId = "DST.Input.Columns[ID]", CachedName = "ID", LineageId = sortConnectsToDestination ? "LIN_SORT_ID" : "LIN_ID" }] }],
        };

        var components = new List<PipelineComponentSpec> { source, sort, destination };
        var paths = new List<PipelinePathSpec>();

        if (sortConnectsToDestination)
        {
            paths.Add(new PipelinePathSpec { RefId = "P1", StartId = "SRC.Output", EndId = "SORT.Input" });
            paths.Add(new PipelinePathSpec { RefId = "P2", StartId = "SORT.Output", EndId = "DST.Input" });
        }
        else
        {
            // Sort exists as a component but is never wired to anything -- the destination is
            // fed directly from the source, matching how PlanDataFlow's own destination search
            // finds it (ANY destination-shaped component in the pipeline, regardless of what
            // feeds it) independent of whether the disconnected Sort is reachable from it.
            paths.Add(new PipelinePathSpec { RefId = "P1", StartId = "SRC.Output", EndId = "DST.Input" });
        }

        if (secondSort)
        {
            var sort2 = BuildSort("SORT2", "SORT_ById2", sortKeys ?? [new SortKeySpec { ColumnName = "ID", Position = 1 }], "LIN_ID", "LIN_SORT2_ID");
            components.Add(sort2);
            // Not wired anywhere -- its mere presence is enough to trip the "more than one Sort"
            // gate, which fires before any reachability walk.
        }

        var pipeline = new PipelineSpec { Components = components, Paths = paths };

        return new PackageSpec
        {
            ObjectName = "P",
            SourceDtsxPath = "P.dtsx",
            Sha256 = "",
            FileSizeBytes = 0,
            LastWriteTimeUtc = default,
            ProtectionLevelRaw = 0,
            ProtectionLevelName = "DontSaveSensitive",
            Coverage = new CoverageStats { TotalElements = 0, UnmappedElements = 0, ExcludedElements = 0, CoveragePercent = 100 },
            Executables =
            [
                new ExecutableSpec
                {
                    RefId = @"Package\DFT_Sort",
                    ExecutableType = "Microsoft.Pipeline",
                    ObjectName = "DFT_Sort",
                    DataFlowTask = new DataFlowTaskPayload { Pipeline = pipeline, Lineage = LineageBuilder.Build(pipeline) },
                },
            ],
        };
    }
}
