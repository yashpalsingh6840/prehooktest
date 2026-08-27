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
}
