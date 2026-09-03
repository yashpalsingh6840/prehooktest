namespace Ssis.Extract.Codegen.Tests;

public class ProgramEmitterTests
{
    [Fact]
    public void Emit_WiresUpOneFlow_FromTheRealLoadEmployeesPlan()
    {
        var package = TestFixtures.LoadPackage("LoadEmployees.dtsx");
        var plan = PackagePlanner.Plan(package);
        var flow = plan.Flows.Single();

        var request = new ProgramRequest(
            PackageName: "LoadEmployees",
            RootNamespace: "LoadEmployees",
            DbContextTypeName: "EmployeeDbContext",
            PreLoadStatements: plan.PreLoadStatements,
            PreLoadFileActions: plan.PreLoadFileActions,
            Steps:
            [
                new ProgramFlowStep(new ProgramFlowSpec(flow.TaskName, new CsvFlowSource(flow.FlatFileSource!.Name, "Employees"), "EmployeeCsvRow", "Employee", "EmployeeTransform", new SqlFlowSink())),
            ]);

        var result = ProgramEmitter.Emit(request);

        var file = Assert.Single(result.Files);
        Assert.Equal("Program.cs", file.RelativePath);

        Assert.Contains("const string PackageName = \"LoadEmployees\";", file.Content);
        Assert.Contains("builder.Services.AddEtlDbContext<EmployeeDbContext>();", file.Content);
        Assert.Contains("builder.Services.AddBulkSink<Employee>();", file.Content);
        Assert.Contains("builder.Services.AddEmailNotifications(builder.Configuration);", file.Content);
        Assert.Contains("return new CsvRowSource<EmployeeCsvRow>(\"FF_SRC_Employees\", csvOptions, new EmployeeCsvRowMap());", file.Content);
        Assert.Contains("var employeeFlow = new DataFlowStep<EmployeeCsvRow, Employee>(", file.Content);
        Assert.Contains("PreLoadStatements: [\"TRUNCATE TABLE dbo.Employee;\"],", file.Content);
        Assert.Contains("Steps: [employeeFlow],", file.Content);
        Assert.Contains("PreLoadFileActions: []);", file.Content);
        Assert.Contains("return ExitCode.Success;", file.Content);
        Assert.Contains("return ExitCode.LoadFailed;", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_WiresUpBothFlowsInOrder_FromTheRealLoadReferenceDataPlan()
    {
        var package = TestFixtures.LoadPackage("LoadReferenceData.dtsx");
        var plan = PackagePlanner.Plan(package);

        var request = new ProgramRequest(
            PackageName: "LoadReferenceData",
            RootNamespace: "LoadReferenceData",
            DbContextTypeName: "ReferenceDataDbContext",
            PreLoadStatements: plan.PreLoadStatements,
            PreLoadFileActions: plan.PreLoadFileActions,
            Steps:
            [
                new ProgramFlowStep(new ProgramFlowSpec(plan.Flows[0].TaskName, new CsvFlowSource(plan.Flows[0].FlatFileSource!.Name, "Department"), "DepartmentCsvRow", "Department", "DepartmentTransform", new SqlFlowSink())),
                new ProgramFlowStep(new ProgramFlowSpec(plan.Flows[1].TaskName, new CsvFlowSource(plan.Flows[1].FlatFileSource!.Name, "Designation"), "DesignationCsvRow", "Designation", "DesignationTransform", new SqlFlowSink())),
            ]);

        var result = ProgramEmitter.Emit(request);
        var file = Assert.Single(result.Files);

        Assert.Contains("builder.Services.AddBulkSink<Department>();", file.Content);
        Assert.Contains("builder.Services.AddBulkSink<Designation>();", file.Content);
        Assert.Contains("var departmentFlow = new DataFlowStep<DepartmentCsvRow, Department>(", file.Content);
        Assert.Contains("var designationFlow = new DataFlowStep<DesignationCsvRow, Designation>(", file.Content);
        Assert.Contains(
            "PreLoadStatements: [\"TRUNCATE TABLE dbo.Department; TRUNCATE TABLE dbo.Designation;\"],",
            file.Content);
        Assert.Contains("Steps: [departmentFlow, designationFlow],", file.Content);
        Assert.Contains("PreLoadFileActions: []);", file.Content);

        // Department must be wired (and appear in Steps) before Designation -- matches the
        // real package's own topological order (PackagePlanner already proved this ordering
        // in PackagePlannerTests; this asserts ProgramEmitter preserves it into the DI wiring).
        Assert.True(file.Content.IndexOf("var departmentFlow", StringComparison.Ordinal)
                  < file.Content.IndexOf("var designationFlow", StringComparison.Ordinal));

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_ReportsAGap_WhenNoFlowsAreGiven()
    {
        var request = new ProgramRequest("Empty", "Empty", "EmptyDbContext", [], [], []);

        var result = ProgramEmitter.Emit(request);

        Assert.Empty(result.Files);
        Assert.Single(result.Gaps);
    }

    [Fact]
    public void Emit_WiresAPostFlowSqlStep_AfterTheFlowItFollows()
    {
        var request = new ProgramRequest(
            PackageName: "SyntheticParallelShapes",
            RootNamespace: "SyntheticParallelShapes",
            DbContextTypeName: "SyntheticParallelShapesDbContext",
            PreLoadStatements: ["IF OBJECT_ID('dbo.SyntheticLoadATemp') IS NULL CREATE TABLE dbo.SyntheticLoadATemp (ID INT);"],
            PreLoadFileActions: [],
            Steps:
            [
                new ProgramFlowStep(new ProgramFlowSpec("DFT_LoadA", new CsvFlowSource("FF_SRC_LoadA", "LoadA"), "LoadACsvRow", "LoadA", "LoadATransform", new SqlFlowSink())),
                new ProgramSqlStep("SQL_UpdateLoadA", "UPDATE dbo.SyntheticLoadATemp SET EntryDate = EntryDate;"),
            ]);

        var result = ProgramEmitter.Emit(request);
        var file = Assert.Single(result.Files);

        Assert.Contains("var loadAFlow = new DataFlowStep<LoadACsvRow, LoadA>(", file.Content);
        Assert.Contains(
            "var sQL_UpdateLoadAStep = new ExecuteSqlStep(\"SQL_UpdateLoadA\", \"UPDATE dbo.SyntheticLoadATemp SET EntryDate = EntryDate;\", sp.GetRequiredService<ILogger<ExecuteSqlStep>>());",
            file.Content);
        Assert.Contains("Steps: [loadAFlow, sQL_UpdateLoadAStep],", file.Content);
        Assert.Contains("PreLoadFileActions: []);", file.Content);

        // The local variable declaration order must also put the SQL step after the flow --
        // it references the local, so declaring it out of order wouldn't even compile.
        Assert.True(file.Content.IndexOf("var loadAFlow", StringComparison.Ordinal)
                  < file.Content.IndexOf("var sQL_UpdateLoadAStep", StringComparison.Ordinal));

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_WiresASqlSourcedFlow_ViaSqlRowSource()
    {
        var request = new ProgramRequest(
            PackageName: "SyntheticOleDbSourceTransform",
            RootNamespace: "SyntheticOleDbSourceTransform",
            DbContextTypeName: "SyntheticOleDbSourceTransformDbContext",
            PreLoadStatements: [],
            PreLoadFileActions: [],
            Steps:
            [
                new ProgramFlowStep(new ProgramFlowSpec(
                    "DFT_Load",
                    new SqlFlowSource("OLE DB Source", "SELECT [OrderID] AS [OrderID], [Amount] AS [Amount] FROM [dbo].[SyntheticOleDbSourceInput]"),
                    "OrderSqlRow", "Order", "OrderTransform", new SqlFlowSink())),
            ]);

        var result = ProgramEmitter.Emit(request);
        var file = Assert.Single(result.Files);

        Assert.Contains("using Etl.Core.Data;", file.Content);
        Assert.Contains("using SyntheticOleDbSourceTransform.Sql;", file.Content);
        Assert.DoesNotContain("using Etl.Core.Csv;", file.Content);
        Assert.Contains("var dbOptions = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;", file.Content);
        Assert.Contains(
            "var sqlOptions = new SqlSourceOptions { ConnectionString = SqlConnectionStringFactory.Build(dbOptions), CommandText = \"SELECT [OrderID] AS [OrderID], [Amount] AS [Amount] FROM [dbo].[SyntheticOleDbSourceInput]\" };",
            file.Content);
        Assert.Contains("return new SqlRowSource<OrderSqlRow>(\"OLE DB Source\", sqlOptions, OrderSqlRowReader.Read, sp.GetRequiredService<IUnitOfWork>());", file.Content);
        Assert.Contains("var orderFlow = new DataFlowStep<OrderSqlRow, Order>(", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }
}
