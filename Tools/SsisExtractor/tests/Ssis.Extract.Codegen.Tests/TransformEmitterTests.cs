using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Codegen.Tests;

public class TransformEmitterTests
{
    [Fact]
    public void Emit_ReportsAGap_ForAPlainPassthroughColumn_WhoseSourceAndDestinationClrTypesDiffer()
    {
        // The exact real shape RBC_Demo_ETL's own DFT_ExcelImport hits: Excel Source always
        // buffers a numeric column as r8/double (Excel/ACE OLEDB has no narrower numeric type
        // at all), but the real destination table's own CustomerID column is i4/int -- SSIS's
        // own OLE DB provider coerces this implicitly at insert time; a plain `row.ColumnName`
        // passthrough in generated C# cannot (double->int is not an implicit C# conversion).
        // Confirmed as a real CS0266 compile error by actually building a fixture reproducing
        // this pairing, not assumed -- see CLAUDE.md's own "Excel Source" section.
        var destination = new PipelineComponentSpec
        {
            RefId = "DST_Test",
            Name = "OLE DB Destination",
            ComponentClassId = "Microsoft.OLEDBDestination",
            Inputs =
            [
                new PipelineInputSpec
                {
                    RefId = "DST_Test.Inputs[OLE DB Destination Input]",
                    Name = "OLE DB Destination Input",
                    Columns =
                    [
                        new PipelineInputColumnSpec
                        {
                            RefId = "DST_Test.Inputs[OLE DB Destination Input].Columns[CustomerID]",
                            CachedName = "CustomerID",
                            CachedDataType = "r8",
                            LineageId = "SRC.Outputs[Output].Columns[CustomerID]",
                            ExternalMetadataColumnId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[CustomerID]",
                        },
                        new PipelineInputColumnSpec
                        {
                            RefId = "DST_Test.Inputs[OLE DB Destination Input].Columns[ReviewedBy]",
                            CachedName = "ReviewedBy",
                            CachedDataType = "wstr",
                            LineageId = "SRC.Outputs[Output].Columns[ReviewedBy]",
                            ExternalMetadataColumnId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[ReviewedBy]",
                        },
                    ],
                    ExternalMetadataColumns =
                    [
                        new PipelineExternalMetadataColumnSpec { RefId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[CustomerID]", Name = "CustomerID", DataType = "i4" },
                        new PipelineExternalMetadataColumnSpec { RefId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[ReviewedBy]", Name = "ReviewedBy", DataType = "wstr", Length = 50 },
                    ],
                },
            ],
        };
        var pipeline = new PipelineSpec { Components = [destination] };

        var request = new TransformRequest(
            "Generated.Mapping", "DripEligibilityTransform",
            "Generated.Excel", "DripRow",
            "Generated.Model", "DripEligibility",
            "Generated.Ssis",
            pipeline, [], destination);

        var result = TransformEmitter.Emit(request);

        var gap = Assert.Single(result.Result.Gaps);
        Assert.Equal("DripEligibility.CustomerID", gap.Location);
        Assert.Contains("buffered as double but the destination column is int", gap.Reason);

        // The MISMATCHED column is dropped from the assignments, but the file still generates
        // with every OTHER column -- same per-column-gap shape as an untranslatable Derived
        // Column expression, not a whole-flow failure.
        var file = Assert.Single(result.Result.Files);
        Assert.DoesNotContain("CustomerID", file.Content);
        Assert.Contains("ReviewedBy = row.ReviewedBy,", file.Content);
        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }


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
