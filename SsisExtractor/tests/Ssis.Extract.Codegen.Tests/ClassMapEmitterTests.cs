namespace Ssis.Extract.Codegen.Tests;

public class ClassMapEmitterTests
{
    [Fact]
    public void Emit_MapsEveryColumnByName_FromTheRealEmployeesCsvConnectionManager()
    {
        var package = TestFixtures.LoadPackage("LoadEmployees.dtsx");
        var cm = TestFixtures.FindConnectionManager(package, "CM_EmployeesCsv");

        var result = ClassMapEmitter.Emit("LoadEmployees.Csv", "EmployeeCsvRow", cm);

        Assert.Empty(result.Gaps);
        var file = Assert.Single(result.Files);
        Assert.Equal("Csv/EmployeeCsvRowMap.cs", file.RelativePath);

        Assert.Contains("public sealed class EmployeeCsvRowMap : ClassMap<EmployeeCsvRow>", file.Content);
        Assert.Contains("Map(m => m.EmployeeID).Name(\"EmployeeID\");", file.Content);
        Assert.Contains("Map(m => m.FirstName).Name(\"FirstName\");", file.Content);
        Assert.Contains("Map(m => m.State).Name(\"State\");", file.Content);

        // No per-column date format is derivable from a .dtsx -- see this emitter's own
        // doc comment for why that's a deliberate omission, not a missed feature.
        Assert.DoesNotContain("TypeConverterOption", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_MapsBothColumns_FromTheRealDepartmentCsvConnectionManager()
    {
        var package = TestFixtures.LoadPackage("LoadReferenceData.dtsx");
        var cm = TestFixtures.FindConnectionManager(package, "CM_DepartmentCsv");

        var result = ClassMapEmitter.Emit("LoadReferenceData.Csv", "DepartmentCsvRow", cm);

        Assert.Empty(result.Gaps);
        var file = Assert.Single(result.Files);
        Assert.Contains("Map(m => m.DepartmentID).Name(\"DepartmentID\");", file.Content);
        Assert.Contains("Map(m => m.DepartmentCode).Name(\"DepartmentCode\");", file.Content);
        Assert.Contains("Map(m => m.HeadCount).Name(\"HeadCount\");", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }
}
