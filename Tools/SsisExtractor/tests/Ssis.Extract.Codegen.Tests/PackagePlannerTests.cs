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
        Path.GetDirectoryName(ThisFilePath())!, "..", "..", "..", "..", "SSIS"));

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
        // The Execute SQL Task lives INSIDE the Sequence Container -- proves nested pre-load
        // SQL is folded into the same flat list a root-level one would be.
        Assert.Equal(["TRUNCATE TABLE dbo.SyntheticNestedTarget;"], plan.PreLoadStatements);

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
        Assert.Equal(["TRUNCATE TABLE dbo.Employee;"], plan.PreLoadStatements);

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
        Assert.Equal(["TRUNCATE TABLE dbo.Department; TRUNCATE TABLE dbo.Designation;"], plan.PreLoadStatements);

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

        Assert.Contains(
            "IF OBJECT_ID('dbo.SyntheticLoadATemp') IS NULL\nCREATE TABLE dbo.SyntheticLoadATemp (ID INT NOT NULL, Name NVARCHAR(50) NOT NULL, Amount DECIMAL(12,2) NOT NULL, EntryDate DATE NOT NULL);",
            plan.PreLoadStatements);
        Assert.DoesNotContain(plan.PreLoadStatements, sql => sql.Contains("UPDATE dbo.SyntheticLoadATemp"));

        var flowStepIndex = plan.Steps.FindIndex(s => s is FlowStep flow && flow.Flow.TaskName == "DFT_LoadA");
        var sqlStepIndex = plan.Steps.FindIndex(s => s is SqlStep sql && sql.TaskName == "SQL_UpdateLoadA");
        Assert.True(flowStepIndex >= 0, "DFT_LoadA should be a FlowStep in plan.Steps");
        Assert.True(sqlStepIndex >= 0, "SQL_UpdateLoadA should be a SqlStep in plan.Steps");
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
        Assert.Equal(["TRUNCATE TABLE dbo.SyntheticFileSystemTaskTarget;"], plan.PreLoadStatements);

        var preLoadAction = Assert.Single(plan.PreLoadFileActions);
        Assert.Equal("Copy", preLoadAction.Operation);
        Assert.EndsWith("source.txt", preLoadAction.SourcePath);
        Assert.EndsWith("archived-preload.txt", preLoadAction.DestinationPath);
        Assert.True(preLoadAction.Overwrite);

        var postFlowStep = Assert.Single(plan.Steps, s => s is FileSystemStep);
        var fsStep = (FileSystemStep)postFlowStep;
        Assert.Equal("FST_PostLoadCopy", fsStep.TaskName);
        Assert.Equal("Copy", fsStep.Action.Operation);
        Assert.EndsWith("archived-postflow.txt", fsStep.Action.DestinationPath);

        // The post-flow File System Task must come after the flow in Steps -- same ordering
        // guarantee SqlStep already has (Plan_RoutesAnExecuteSqlTaskAfterADataFlow_...).
        var flowStepIndex = plan.Steps.FindIndex(s => s is FlowStep);
        var fsStepIndex = plan.Steps.FindIndex(s => s is FileSystemStep);
        Assert.True(fsStepIndex > flowStepIndex);
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

        // The only gap is the new non-blocking parallelism advisory: this fixture's three root
        // flows carry no precedence constraint between them, so SSIS ran them concurrently while
        // the generated PackageRunner runs them in sequence. Neither flat-file flow gaps.
        var parallelism = Assert.Single(plan.Gaps);
        Assert.False(parallelism.IsBlocking);
        Assert.EndsWith(".Parallelism", parallelism.Location);
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

        // The only gap is the new non-blocking parallelism advisory: this fixture's two root
        // executables (DFT_Load and FEL_SampleFiles) carry no precedence constraint between
        // them, so SSIS ran them concurrently while the generated PackageRunner runs them in
        // sequence. No ForEach-Loop-specific gap.
        var parallelism = Assert.Single(plan.Gaps);
        Assert.False(parallelism.IsBlocking);
        Assert.EndsWith(".Parallelism", parallelism.Location);
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
        Assert.Equal("Region", flow.Aggregate.GroupByOutputColumnName);
        Assert.Equal("Region", flow.Aggregate.GroupBySourceColumnName);
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
        Assert.Equal("Region", flow.Aggregate!.GroupByOutputColumnName);
        Assert.Equal("Region", flow.Aggregate.GroupBySourceColumnName);
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
