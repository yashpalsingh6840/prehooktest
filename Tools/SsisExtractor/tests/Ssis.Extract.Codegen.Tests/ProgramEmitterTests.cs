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
            PreLoadStatements: [new ProgramPreLoadStatement("SQL_TruncateTarget", "SQL_TruncateTargetStatement")],
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
        Assert.Contains("await uow.ExecuteSqlAsync(SQL_TruncateTargetStatement.BuildStatement(), CancellationToken.None);", file.Content);
        Assert.Contains("results.Add(await employeeFlow.RunAsync(uow, load, CancellationToken.None));", file.Content);
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
            PreLoadStatements: [new ProgramPreLoadStatement("SQL_TruncateTargets", "SQL_TruncateTargetsStatement")],
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
            "await uow.ExecuteSqlAsync(SQL_TruncateTargetsStatement.BuildStatement(), CancellationToken.None);",
            file.Content);
        Assert.Contains("results.Add(await departmentFlow.RunAsync(uow, load, CancellationToken.None));", file.Content);
        Assert.Contains("results.Add(await designationFlow.RunAsync(uow, load, CancellationToken.None));", file.Content);

        // Department must run before Designation -- matches the real package's own topological
        // order (PackagePlanner already proved this ordering in PackagePlannerTests; this asserts
        // ProgramEmitter preserves it into the emitted run calls).
        Assert.True(file.Content.IndexOf("results.Add(await departmentFlow", StringComparison.Ordinal)
                  < file.Content.IndexOf("results.Add(await designationFlow", StringComparison.Ordinal));

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
            PreLoadStatements: [new ProgramPreLoadStatement("SQL_CreateLoadATemp", "SQL_CreateLoadATempStatement")],
            PreLoadFileActions: [],
            Steps:
            [
                new ProgramFlowStep(new ProgramFlowSpec("DFT_LoadA", new CsvFlowSource("FF_SRC_LoadA", "LoadA"), "LoadACsvRow", "LoadA", "LoadATransform", new SqlFlowSink())),
                new ProgramSqlStep("SQL_UpdateLoadA", "SQL_UpdateLoadAStatement"),
            ]);

        var result = ProgramEmitter.Emit(request);
        var file = Assert.Single(result.Files);

        Assert.Contains("var loadAFlow = new DataFlowStep<LoadACsvRow, LoadA>(", file.Content);
        // Same treatment for a pre-load statement: a call to its own named statement class, never
        // an inline SQL literal (SqlStatementBuilderEmitter's own job is producing that class's
        // file, which this hand-built request bypasses entirely).
        Assert.Contains("await uow.ExecuteSqlAsync(SQL_CreateLoadATempStatement.BuildStatement(), CancellationToken.None);", file.Content);
        // ProgramEmitter only ever wires a call to the named statement class (SqlStatementBuilderEmitter's
        // own job is producing that class's file, which this hand-built request bypasses entirely) --
        // never an inline SQL literal.
        Assert.Contains(
            "var sQL_UpdateLoadAStep = new ExecuteSqlStep(\"SQL_UpdateLoadA\", SQL_UpdateLoadAStatement.BuildStatement(), sp.GetRequiredService<ILogger<ExecuteSqlStep>>());",
            file.Content);
        Assert.Contains("results.Add(await loadAFlow.RunAsync(uow, load, CancellationToken.None));", file.Content);
        Assert.Contains("results.Add(await sQL_UpdateLoadAStep.RunAsync(uow, load, CancellationToken.None));", file.Content);

        // Both the local variable construction order AND the RunAsync call order must put the SQL
        // step after the flow -- matching the plan's own topological order (the two locals are
        // otherwise independent of one another; this ordering is preserved, not required to compile).
        Assert.True(file.Content.IndexOf("var loadAFlow", StringComparison.Ordinal)
                  < file.Content.IndexOf("var sQL_UpdateLoadAStep", StringComparison.Ordinal));
        Assert.True(file.Content.IndexOf("results.Add(await loadAFlow", StringComparison.Ordinal)
                  < file.Content.IndexOf("results.Add(await sQL_UpdateLoadAStep", StringComparison.Ordinal));

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
