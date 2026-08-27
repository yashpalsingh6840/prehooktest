namespace Ssis.Extract.Codegen.Tests;

public class TransformEmitterTests
{
    [Fact]
    public void Emit_ReproducesTheHandWrittenEmployeeTransform_FromTheRealLoadEmployeesPackage()
    {
        var package = TestFixtures.LoadPackage("LoadEmployees.dtsx");
        var pipeline = TestFixtures.FindPipeline(package, "DFT_LoadEmployees");
        var derivedColumn = TestFixtures.FindComponent(package, "Microsoft.DerivedColumn", "DER_MergeColumns");
        var destination = TestFixtures.FindComponent(package, "Microsoft.OLEDBDestination", "OLEDST_Employee");

        var request = new TransformRequest(
            MappingNamespace: "LoadEmployees.Mapping",
            TransformClassName: "EmployeeTransform",
            RowTypeNamespace: "LoadEmployees.Csv",
            RowTypeName: "EmployeeCsvRow",
            EntityNamespace: "LoadEmployees.Model",
            EntityName: "Employee",
            SsisFnNamespace: "LoadEmployees.Ssis",
            Pipeline: pipeline,
            DerivedColumns: [derivedColumn],
            DestinationComponent: destination);

        var result = TransformEmitter.Emit(request);

        Assert.Empty(result.Result.Gaps);
        Assert.Equal(["Str", "Substring", "Upper"], result.SsisFunctionsUsed.OrderBy(x => x));

        var file = Assert.Single(result.Result.Files);
        Assert.Equal("Mapping/EmployeeTransform.cs", file.RelativePath);
        Assert.Contains("public sealed class EmployeeTransform : IRowTransform<EmployeeCsvRow, Employee>", file.Content);

        // Computed columns -- exact same expressions EmployeeTransform.cs was hand-written with.
        Assert.Contains(
            "FullName = WidthGuard.Wstr($\"{row.FirstName} {row.LastName}\", 101, nameof(Employee.FullName), ctx.RowNumber),",
            file.Content);
        Assert.Contains(
            "Location = WidthGuard.Wstr($\"{row.City}, {row.State}\", 60, nameof(Employee.Location), ctx.RowNumber),",
            file.Content);
        Assert.Contains(
            "EmployeeKey = WidthGuard.Wstr($\"{SsisFn.Upper(SsisFn.Substring(row.Department, 1, 3))}-{SsisFn.Str(row.EmployeeID)}\", 20, nameof(Employee.EmployeeKey), ctx.RowNumber),",
            file.Content);
        Assert.Contains("LoadedAtUtc = ctx.LoadedAtUtc,", file.Content);

        // Plain passthrough columns -- never re-derived, straight from the row.
        Assert.Contains("EmployeeID = row.EmployeeID,", file.Content);
        Assert.Contains("Department = row.Department,", file.Content);
        Assert.Contains("HireDate = row.HireDate,", file.Content);
        Assert.Contains("Salary = row.Salary,", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_ReproducesTheHandWrittenDepartmentTransform_NoSubstringUsed()
    {
        var package = TestFixtures.LoadPackage("LoadReferenceData.dtsx");
        var pipeline = TestFixtures.FindPipeline(package, "DFT_LoadDepartment");
        var derivedColumn = TestFixtures.FindComponent(package, "Microsoft.DerivedColumn", "DER_DepartmentKey");
        var destination = TestFixtures.FindComponent(package, "Microsoft.OLEDBDestination", "OLEDST_Department");

        var request = new TransformRequest(
            "LoadReferenceData.Mapping", "DepartmentTransform",
            "LoadReferenceData.Csv", "DepartmentCsvRow",
            "LoadReferenceData.Model", "Department",
            "LoadReferenceData.Ssis",
            pipeline, [derivedColumn], destination);

        var result = TransformEmitter.Emit(request);

        Assert.Empty(result.Result.Gaps);
        Assert.Equal(["Str", "Upper"], result.SsisFunctionsUsed.OrderBy(x => x));

        var file = Assert.Single(result.Result.Files);
        Assert.Contains(
            "DepartmentKey = WidthGuard.Wstr($\"{SsisFn.Upper(row.DepartmentCode)}-{SsisFn.Str(row.DepartmentID)}\", 25, nameof(Department.DepartmentKey), ctx.RowNumber),",
            file.Content);
        Assert.Contains("CostCenter = row.CostCenter,", file.Content);
        Assert.Contains("HeadCount = row.HeadCount,", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_ReproducesTheHandWrittenDesignationTransform_KeyedOffJobLevelNotDesignationId()
    {
        var package = TestFixtures.LoadPackage("LoadReferenceData.dtsx");
        var pipeline = TestFixtures.FindPipeline(package, "DFT_LoadDesignation");
        var derivedColumn = TestFixtures.FindComponent(package, "Microsoft.DerivedColumn", "DER_DesignationKey");
        var destination = TestFixtures.FindComponent(package, "Microsoft.OLEDBDestination", "OLEDST_Designation");

        var request = new TransformRequest(
            "LoadReferenceData.Mapping", "DesignationTransform",
            "LoadReferenceData.Csv", "DesignationCsvRow",
            "LoadReferenceData.Model", "Designation",
            "LoadReferenceData.Ssis",
            pipeline, [derivedColumn], destination);

        var result = TransformEmitter.Emit(request);

        Assert.Empty(result.Result.Gaps);
        var file = Assert.Single(result.Result.Files);

        // DER_DesignationKey keys off JobLevel, not DesignationID -- easy to get backwards by
        // pattern-matching DepartmentTransform; this is the same fact
        // LoadReferenceData.Tests\DesignationTransformTests.cs pins on the hand-written side.
        Assert.Contains(
            "DesignationKey = WidthGuard.Wstr($\"{SsisFn.Upper(row.DesignationCode)}-{SsisFn.Str(row.JobLevel)}\", 25, nameof(Designation.DesignationKey), ctx.RowNumber),",
            file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }
}
