using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;

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
        Assert.Equal("OLE DB Destination", branches[0].Destination.Name);
        Assert.Equal("[dbo].[SyntheticHighValue]", branches[0].Destination.OleDbDestination?.OpenRowset);

        // The default branch is always last and carries no condition of its own.
        Assert.Equal("LowValue", branches[1].OutputName);
        Assert.Null(branches[1].FriendlyExpression);
        Assert.Equal("OLE DB Destination 1", branches[1].Destination.Name);
        Assert.Equal("[dbo].[SyntheticLowValue]", branches[1].Destination.OleDbDestination?.OpenRowset);

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
        Assert.Equal("[dbo].[SyntheticRemergeTarget]", highBranch.Destination.OleDbDestination?.OpenRowset);
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
        Assert.Equal("Microsoft.OLEDBDestination", canada.Destination.ComponentClassId);
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
        var count = Assert.Single(flow.Aggregate.Counts);
        Assert.Equal("CustomerCount", count.OutputColumnName);
        Assert.Equal("CustomerID", count.SourceColumnName);
    }

    [Fact]
    public void Plan_ReportsAGap_ForAnAggregate_UsingAnUnsupportedAggregationType()
    {
        // SyntheticAggregateUnsupportedType.dtsx: a byte-preserving derived copy of
        // SyntheticAggregate.dtsx with its own CustomerCount column's AggregationType changed
        // from 1 (Count, supported) to 3 (an unsupported value -- Sum/Average/Minimum/Maximum/
        // CountDistinct/CountAll are none of them evidenced or measured) -- reported as an
        // explicit gap rather than guessed.
        var package = LoadSyntheticFixture("SyntheticAggregateUnsupportedType.dtsx");

        var plan = PackagePlanner.Plan(package);

        Assert.Empty(plan.Flows);
        var gap = Assert.Single(plan.Gaps, g => g.Location == "DFT_Aggregate");
        Assert.Contains("AGG_ByRegion", gap.Reason);
        Assert.Contains("unsupported AggregationType", gap.Reason);
        Assert.Contains("AggregationType=3", gap.Reason);
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
        Assert.Equal("Microsoft.OLEDBDestination", flow.Multicast.Branches[0].Destination.ComponentClassId);
        Assert.Equal("Microsoft.FlatFileDestination", flow.Multicast.Branches[1].Destination.ComponentClassId);
    }
}
